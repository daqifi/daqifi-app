using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Exporter;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OxyPlot;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// Mobile "APP LOGS" half of the Storage tab — the phone projection of the desktop
/// Logged Data pane's locally-logged session list (#7). Reads the same
/// <see cref="LoggingManager.Instance"/> session collection the desktop pane binds
/// to (populated on mobile by <c>App.InitializeMobile</c>) and offers the
/// non-destructive **export** action via the reused
/// <see cref="OptimizedLoggingSessionExporter"/> + the platform save picker.
///
/// The inline session PLOT VIEWER and DELETE / DELETE-ALL (#7 phase-2) reuse the desktop's
/// WPF-free building blocks directly — <see cref="SessionDataRepository"/> (load + transactional
/// delete over the injected DbContext factory) and <see cref="PlotModelFactory"/> for a styled
/// OxyPlot model, plus the reusable <see cref="ConfirmOverlayViewModel"/> for destructive confirms
/// (the desktop MahApps MessageBoxService is main-window-bound and NOT reusable on mobile). The
/// heavy DB read + plot build run off the UI thread; the viewer uses the same two-phase strategy
/// as the desktop (fast initial batch, full-range downsample for large sessions) so the phone never
/// materializes millions of rows.
/// </summary>
public partial class LoggedSessionsMobileViewModel : ObservableObject
{
    private readonly IAppLogger _appLogger = AppLogger.Instance;

    /// <summary>Set by the view (which owns the TopLevel): shows the save picker for a
    /// single-session export, given a suggested name WITHOUT an extension (the picker adds
    /// it), and returns the picked file, or null if cancelled.</summary>
    /// <remarks>A storage item, not a path: on Android the pick is a <c>content://</c> URI
    /// that has no path at all (issue #587). <see cref="StorageExportWriter"/> writes through
    /// the item's own stream.</remarks>
    public Func<string, Task<IStorageFile?>>? SaveFileResolver { get; set; }

    /// <summary>Set by the view: shows the folder picker for a multi-session "Export all"
    /// (one CSV per session) and returns the picked folder, or null if cancelled.</summary>
    public Func<Task<IStorageFolder?>>? SaveFolderResolver { get; set; }

    public ObservableCollection<LoggingSession> Sessions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessions))]
    [NotifyPropertyChangedFor(nameof(HasNoSessions))]
    private bool _loaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSessions))]
    [NotifyPropertyChangedFor(nameof(LoadFailed))]
    private bool _loadFailedFlag;

    public bool HasSessions => Sessions.Count > 0;

    // Only the definitive "no sessions" empty state — NOT a failed load, which would
    // otherwise render as "NO LOGGED SESSIONS" and hide that the list couldn't load.
    public bool HasNoSessions => Loaded && !LoadFailedFlag && Sessions.Count == 0;

    /// <summary>True when the last load threw — the view shows an error + Retry instead
    /// of the definitive empty state.</summary>
    public bool LoadFailed => LoadFailedFlag;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // --- Session viewer + delete (#7 phase-2) ---

    /// <summary>Nested confirm overlay for destructive delete actions. Reuses the WPF-free
    /// <see cref="ConfirmOverlayViewModel"/> the desktop panes use; the view binds to
    /// <c>ConfirmOverlay.*</c> and the awaited <see cref="ConfirmOverlayViewModel.ShowAsync"/>
    /// resolves when the user taps DELETE or cancels.</summary>
    public ConfirmOverlayViewModel ConfirmOverlay { get; } = new();

    /// <summary>Per-channel legend chips (name + colour swatch) for the currently-viewed session,
    /// mirroring the desktop's custom legend (the plot's own legend stays disabled).</summary>
    public ObservableCollection<ViewerChannel> ViewerChannels { get; } = new();

    /// <summary>The OxyPlot model for the viewed session, or null while loading / when it has no data.</summary>
    [ObservableProperty]
    private PlotModel? _sessionPlotModel;

    /// <summary>True while the session-viewer overlay is open.</summary>
    [ObservableProperty]
    private bool _isViewerOpen;

    /// <summary>True while the viewer is loading the session's samples off the UI thread.</summary>
    [ObservableProperty]
    private bool _isViewerLoading;

    /// <summary>The viewed session's name, shown in the viewer header.</summary>
    [ObservableProperty]
    private string _viewerTitle = string.Empty;

    /// <summary>Viewer status line (loading / empty / error); empty once the plot is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewerHasStatus))]
    private string _viewerStatus = string.Empty;

    public bool ViewerHasStatus => !string.IsNullOrEmpty(ViewerStatus);

    /// <summary>Lazily-built read/delete repository over the mobile DI DbContext factory.</summary>
    private SessionDataRepository? _repository;

    /// <summary>The DbContext factory the repository was built over, cached for the static
    /// single-timestamp value-spread helper (which takes the factory directly).</summary>
    private IDbContextFactory<LoggingContext>? _factory;

    /// <summary>Reads the committed session list. A pure SELECT; see <see cref="LoadSessionsAsync"/>.</summary>
    private readonly Func<IReadOnlyList<LoggingSession>> _loadSnapshot;

    /// <summary>Runs the bound-collection mutation on the UI thread; completes once it has run.</summary>
    private readonly Func<Action, Task> _invokeOnUiAsync;

    public LoggedSessionsMobileViewModel()
        : this(
            static () => LoggingManager.Instance.GetLoggingSessionsSnapshot(),
            static action => Dispatcher.UIThread.InvokeAsync(action).GetTask())
    {
    }

    /// <summary>
    /// Test seam (review of PR #609). The unit tests have no Avalonia lifetime, so
    /// <c>LoggingManager.Instance</c> throws and <c>Dispatcher.UIThread.InvokeAsync</c> never runs
    /// its action. The tests pass a snapshot source and an inline UI invoker instead; everything
    /// else is the real code.
    /// </summary>
    internal LoggedSessionsMobileViewModel(
        Func<IReadOnlyList<LoggingSession>> loadSnapshot,
        Func<Action, Task> invokeOnUiAsync)
    {
        _loadSnapshot = loadSnapshot;
        _invokeOnUiAsync = invokeOnUiAsync;
        // Fire-and-forget: Reload hydrates off the UI thread and handles its own
        // errors, so there is nothing to await at construction.
        _ = Reload();
    }

    [RelayCommand]
    private async Task Reload()
    {
        // On (re)entering the list, dismiss any viewer/confirm left open from a prior visit. The shell
        // hides this pane by toggling IsVisible (no detach), so cleanup can't reliably hang off
        // DetachedFromVisualTree on the common exit paths (the Stream/home tab, the APP↔DEVICE pivot);
        // resetting here — Reload runs on every Storage visit — means returning always shows the fresh
        // list and releases a large session's retained plot model. Harmless on the first load (nothing
        // is open yet). Runs before the IsBusy guard so a re-entry still resets even mid-load.
        CloseViewer();
        ConfirmOverlay.Cancel();

        await LoadSessionsAsync();
    }

    /// <summary>
    /// Re-reads the list in place because a phone recording has just been committed to SQLite
    /// (review of PR #609). <see cref="StorageMobileView"/> calls it when the shell raises
    /// <see cref="MobileShellViewModel.RecordingCommitted"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The user can stop a recording and open Logged Data while the Live Graph banner still says
    /// "Saving the recording…". That visit's snapshot is read before the writer commits, and APP
    /// LOGS lists only a session with a committed sample, so the list showed no new recording until
    /// a manual Refresh or another visit, even after the banner said it was saved.
    /// </para>
    /// <para>
    /// Unlike <see cref="Reload"/>, this does not close a session viewer or a confirm that the user
    /// opened meanwhile: nobody re-entered the list, so there is nothing left over to dismiss. Only
    /// the list underneath changes. And it is not dropped while a load, an export or a delete holds
    /// <see cref="IsBusy"/>: a load in flight may have read its snapshot before the commit, so the
    /// re-read runs as soon as the busy operation ends (<see cref="OnIsBusyChanged"/>).
    /// </para>
    /// </remarks>
    internal void RefreshSessions()
    {
        if (IsBusy) { _refreshPending = true; return; }
        _ = LoadSessionsAsync(keepOperationResult: true);
    }

    private bool _refreshPending;

    partial void OnIsBusyChanged(bool value)
    {
        if (value || !_refreshPending) { return; }
        _refreshPending = false;
        // The busy operation that just ended may be an export or a delete that has written its
        // result to the status line. Keep it: the user did not ask for this reload.
        _ = LoadSessionsAsync(keepOperationResult: true);
    }

    /// <summary>The status line a failed load shows. A later successful load clears exactly this
    /// text, and nothing else, when it must keep an operation result.</summary>
    private const string LoadFailedMessage = "Couldn't load logged sessions. Tap Refresh to retry.";

    /// <summary>
    /// The one load path. <see cref="Reload"/> (every visit and the Refresh tap) and
    /// <see cref="RefreshSessions"/> (a committed recording) both read the list here.
    /// </summary>
    /// <param name="keepOperationResult">
    /// True for the automatic refresh after a commit (review of PR #609). A commit can land while
    /// an export or a delete holds <see cref="IsBusy"/>; the queued refresh then runs just after
    /// that operation writes its result ("Exported 1 session to …", "Couldn't delete that session…").
    /// Clearing the status line on success would erase that result before the user could read
    /// it. So this load clears only a previous load error. A user Reload still clears the line.
    /// </param>
    private async Task LoadSessionsAsync(bool keepOperationResult = false)
    {
        if (IsBusy) { return; }
        IsBusy = true;
        try
        {
            // Hydrate straight from the DB, OFF the UI thread. Use the PURE-READ snapshot
            // (not LoadPersistedLoggingSessions): this pane re-reads on every Storage visit,
            // and LoadPersistedLoggingSessions PURGES empty-session rows as a side effect —
            // which would race the SD-card importer and delete its in-flight (0-sample)
            // session. GetLoggingSessionsSnapshot only SELECTs (the in-flight row is excluded
            // by the Samples.Any filter until it has data), so a refresh can't destroy it.
            // It returns a fresh list without touching the shared singleton collection, and
            // the EF Core query runs on a background thread so opening Storage never blocks.
            var loaded = await Task.Run(_loadSnapshot);
            // Marshal the ObservableCollection mutation onto the UI thread explicitly:
            // the await above does not guarantee resuming on the UI thread (e.g. the
            // fire-and-forget ctor path may have no captured context), and Sessions is
            // bound to the UI.
            await _invokeOnUiAsync(() =>
            {
                Sessions.Clear();
                foreach (var session in loaded)
                {
                    Sessions.Add(session);
                }
            });
            LoadFailedFlag = false;
            // Clear any prior load error on success — otherwise a successful retry against an
            // EMPTY db would show the definitive "NO LOGGED SESSIONS" empty state AND the stale
            // "Couldn't load…" error at the same time. An automatic refresh clears only that
            // error and keeps an export or delete result (see keepOperationResult).
            if (!keepOperationResult || StatusMessage == LoadFailedMessage)
            {
                StatusMessage = string.Empty;
            }
        }
        catch (Exception ex)
        {
            // Surface the failure: an empty Sessions list here means the load FAILED, not
            // that there are genuinely no sessions — so flag it and show an error + Retry
            // instead of the definitive "NO LOGGED SESSIONS" empty state.
            _appLogger.Error(ex, "Mobile: failed to load logged sessions");
            LoadFailedFlag = true;
            StatusMessage = LoadFailedMessage;
        }
        finally
        {
            Loaded = true;
            IsBusy = false;
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(HasNoSessions));
        }
    }

    [RelayCommand]
    private Task ExportSession(LoggingSession? session) => ExportSessionsAsync(session);

    [RelayCommand]
    private Task ExportAll() => ExportSessionsAsync(null);

    /// <summary>Exports a single session to a user-picked CSV file, or every session to a
    /// user-picked folder (one <c>{session}.csv</c> per session). Non-destructive; failures
    /// are logged and surfaced on the status line, never thrown.</summary>
    private async Task ExportSessionsAsync(LoggingSession? single)
    {
        // Guard re-entrancy: no overlapping export/reload, and no second picker while one
        // is open. IsBusy is held for the WHOLE operation (picker + write).
        if (IsBusy) { return; }
        if (single is not null ? SaveFileResolver is null : SaveFolderResolver is null) { return; }

        var toExport = single is not null ? new[] { single } : Sessions.ToArray();
        if (toExport.Length == 0) { return; }

        IsBusy = true;
        try
        {
            StatusMessage = "Exporting…";
            var writer = new StorageExportWriter(_appLogger);
            if (single is not null)
            {
                // Single session → one picked file.
                IStorageFile? file;
                try
                {
                    file = await SaveFileResolver!(SuggestedSaveName(single.Name, single.ID));
                }
                catch (Exception ex)
                {
                    _appLogger.Error(ex, "Mobile: export save-picker failed");
                    StatusMessage = "Unable to open the export picker.";
                    return;
                }
                // Null is only ever a cancel now: no picked file is refused for lacking a path.
                if (file is null) { StatusMessage = string.Empty; return; }

                using (file)
                {
                    // One exporter for the operation captures the CSV delimiter ONCE (its ctor
                    // reads DaqifiSettings.CsvDelimiter), so toggling the delimiter in Settings
                    // mid-export can't change it under us.
                    var exporter = new OptimizedLoggingSessionExporter();
                    var wrote = await writer.WriteAsync(file,
                        staging => ExportOne(exporter, single, staging, sessionIndex: 0, totalSessions: 1));
                    // A listed session always has samples, so a false result here means the export
                    // FAILED (unwritable destination, disk full) — not that it was "empty".
                    StatusMessage = wrote
                        ? $"Exported 1 session to {file.Name}."
                        : $"Couldn't export to {file.Name}. Nothing was saved — see logs.";
                }
            }
            else
            {
                // Export all → one CSV per session into a picked folder. Each session must
                // get its OWN file: a completed export REPLACES its destination, so writing
                // every session to a single name would leave only the last one.
                IStorageFolder? folder;
                try
                {
                    folder = await SaveFolderResolver!();
                }
                catch (Exception ex)
                {
                    _appLogger.Error(ex, "Mobile: export folder-picker failed");
                    StatusMessage = "Unable to open the folder picker.";
                    return;
                }
                if (folder is null) { StatusMessage = string.Empty; return; }

                using (folder)
                {
                    // One exporter for the WHOLE batch captures the CSV delimiter once (its ctor
                    // reads DaqifiSettings.CsvDelimiter), so toggling the delimiter in Settings
                    // mid-"Export all" can't produce a folder of mixed-delimiter CSVs.
                    var exporter = new OptimizedLoggingSessionExporter();
                    // Disambiguate duplicate / sanitization-colliding names so two sessions never
                    // target the same file. Shared with the desktop Export dialog, which has to
                    // make the same promise.
                    var namer = new ExportFileNamer();
                    var written = new List<string>();
                    for (var i = 0; i < toExport.Length; i++)
                    {
                        var session = toExport[i];
                        var index = i;
                        var name = await writer.WriteNewAsync(folder, namer.NextCsvName(session.Name, session.ID),
                            staging => ExportOne(exporter, session, staging, sessionIndex: index, totalSessions: toExport.Length));
                        if (name is not null) { written.Add(name); }
                    }

                    StatusMessage = ExportAllStatus(written, toExport.Length, folder.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Mobile: session export failed");
            StatusMessage = "Export failed — see logs.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Session viewer (#7 phase-2): tap a session → plot its logged data ----

    /// <summary>Opens the viewer overlay for a session and loads its samples into an OxyPlot model
    /// off the UI thread (reusing the desktop <see cref="SessionDataRepository"/> +
    /// <see cref="PlotModelFactory"/>). Small sessions plot their full initial batch; large ones use
    /// the full-range downsampled load (~3000 pts/channel) so the phone never materializes millions
    /// of rows. Failures and empty sessions surface on the viewer status line, never thrown.</summary>
    // Monotonic token: each ViewSession/CloseViewer bumps it so an off-thread load can tell whether
    // it is still the current one. Supersedes a stale load (so it can't clobber a newer viewer) AND,
    // with CloseViewer resetting IsViewerLoading, prevents a close-then-tap-new-session from being
    // silently dropped by a re-entry guard the old in-flight load hadn't released yet.
    private int _viewToken;

    // Cancels the previous viewer load when a newer ViewSession/CloseViewer supersedes it, so
    // AllowConcurrentExecutions can't let rapid tapping pile up unbounded concurrent loads. Written
    // only on the UI thread (command handlers); never Disposed (see the ViewSession finally) so that
    // a concurrent Cancel() can never hit a disposed source.
    private CancellationTokenSource? _viewCts;

    // AllowConcurrentExecutions: without it, the generated AsyncRelayCommand reports CanExecute=false
    // while a load's ExecutionTask is in flight, disabling EVERY row's View button — so a
    // close-then-tap-new-session tap during a slow (>100k-sample) load was still silently dropped
    // until the stale load finished. The _viewToken makes concurrent loads safe (newest wins, stale
    // result dropped), so allow them and keep the buttons responsive.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ViewSession(LoggingSession? session)
    {
        if (session is null) { return; }
        var token = ++_viewToken;   // newest wins; supersedes any in-flight load

        // Bound concurrency: cancel the previous (superseded) load so rapid tapping can't pile up
        // unbounded concurrent DB reads + plot builds, then adopt a fresh source for this load.
        _viewCts?.Cancel();
        var cts = _viewCts = new CancellationTokenSource();

        // Open immediately with a loading state; the DB read + plot build run off-thread below.
        ViewerTitle = session.Name;
        ViewerChannels.Clear();
        SessionPlotModel = null;
        ViewerStatus = "Loading…";
        IsViewerOpen = true;
        IsViewerLoading = true;
        try
        {
            var repo = Repo();
            if (repo is null)
            {
                ViewerStatus = "Logged data isn't available on this device.";
                return;
            }

            var built = await Task.Run(() => BuildSessionPlot(repo, session.ID, cts.Token), cts.Token);
            // Marshal the plot-model + legend assignment onto the UI thread: SessionPlotModel is
            // bound to the PlotView and ViewerChannels to the legend, and the await may not resume
            // on the UI thread.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // A newer ViewSession or a CloseViewer ran while this load was off-thread — its token
                // bump means this result is stale, so drop it rather than clobber the newer state.
                if (token != _viewToken) { return; }

                if (built is null)
                {
                    ViewerStatus = "This session has no data to plot.";
                    return;
                }

                foreach (var channel in built.Value.channels) { ViewerChannels.Add(channel); }
                SessionPlotModel = built.Value.model;
                ViewerStatus = string.Empty;
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer view/close (its cancellation) — the newer load owns the UI now.
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Mobile: failed to load session plot");
            // Marshalled: the post-await catch may run off the UI thread, and ViewerStatus is UI-bound.
            OnUi(() => { if (token == _viewToken) { ViewerStatus = "Couldn't load this session's data. See logs."; } });
        }
        finally
        {
            // Deliberately do NOT Dispose `cts`: we never use its WaitHandle or a timer, so there is no
            // unmanaged resource to release, and not disposing removes the Cancel()-vs-Dispose() race —
            // a concurrent CloseViewer/ViewSession only ever Cancel()s a live-or-superseded source (a
            // harmless no-op), never a disposed one. _viewCts holds the newest source until replaced,
            // and GC reclaims superseded ones. Only the UI-bound spinner needs the UI thread + the
            // current-load guard (a superseded load must not clear a newer load's "Loading…").
            OnUi(() => { if (token == _viewToken) { IsViewerLoading = false; } });
        }
    }

    /// <summary>Runs a UI-bound state mutation on the UI thread — inline if already there, else
    /// marshalled. Used by the post-await catch/finally, whose continuation isn't guaranteed to
    /// resume on the UI thread (the success path already marshals via Dispatcher.InvokeAsync).</summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) { action(); }
        else { Dispatcher.UIThread.Post(action); }
    }

    /// <summary>Closes the viewer overlay and releases the plot model + legend so a large session's
    /// points don't linger in memory after the viewer is dismissed.</summary>
    [RelayCommand]
    private void CloseViewer()
    {
        _viewToken++;             // supersede any in-flight load so its result is dropped
        _viewCts?.Cancel();       // and cancel it so it stops early instead of running to completion
        IsViewerOpen = false;
        IsViewerLoading = false;  // release the re-entry spinner so the next View tap isn't dropped
        SessionPlotModel = null;
        ViewerChannels.Clear();
        ViewerStatus = string.Empty;
    }

    /// <summary>Builds a styled OxyPlot model (analog/digital/time axes, dark theme) with one line
    /// series per channel from a session's samples, plus the matching legend chips. Runs entirely on
    /// a background thread — the OxyPlot objects are unattached POCOs until assigned to the bound
    /// property. Returns null for a session that yields no plottable channel. </summary>
    private (PlotModel model, List<ViewerChannel> channels)? BuildSessionPlot(SessionDataRepository repo, int sessionId, CancellationToken ct)
    {
        var initial = repo.LoadInitialSession(sessionId);
        ct.ThrowIfCancellationRequested();
        if (initial.IsEmpty) { return null; }

        var points = initial.Points;
        // The Phase-1 initial batch is capped and only covers the START of a large session, so for
        // sessions past the cap pull a uniformly-sampled full-range view instead (same two-phase
        // strategy the desktop uses). Keep the initial batch if the full-range load found nothing.
        if (initial.TotalSampleCount > SessionDataRepository.INITIAL_LOAD_POINTS)
        {
            // Skip the expensive multi-query full-range load if a newer view/close already superseded us.
            ct.ThrowIfCancellationRequested();
            var seeded = new Dictionary<(string deviceSerial, string channelName), List<DataPoint>>();
            foreach (var channel in initial.Channels)
            {
                seeded[(channel.DeviceSerialNo, channel.ChannelName)] = new List<DataPoint>();
            }

            var firstTime = repo.LoadSampledData(sessionId, initial.Channels.Count, seeded);
            if (firstTime is not null && seeded.Values.Any(p => p.Count > 0))
            {
                points = seeded;
            }
            else if (_factory is not null)
            {
                // Degenerate case: every sample shares one timestamp, so LoadSampledData can't
                // sample a zero-width time range and returns null. Fall back to per-channel MIN/MAX
                // value spreads aggregated across the FULL session (mirrors the desktop) so the plot
                // shows the true value range rather than only the capped initial batch's first rows.
                var channelKeys = initial.Channels
                    .Select(c => (c.DeviceSerialNo, c.ChannelName))
                    .ToList();
                var spreads = SessionDataRepository.LoadSingleTickValueSpread(_factory, sessionId, channelKeys);
                if (spreads.Values.Any(p => p.Count > 0)) { points = spreads; }
            }
        }

        ct.ThrowIfCancellationRequested();
        var factory = new PlotModelFactory();
        var model = factory.CreateMainPlotModel();
        var legend = new List<ViewerChannel>();
        foreach (var channel in initial.Channels)
        {
            if (!points.TryGetValue((channel.DeviceSerialNo, channel.ChannelName), out var channelPoints)
                || channelPoints.Count == 0)
            {
                continue;
            }

            // databaseLogger: null → no minimap sync (there is no minimap on mobile); the series
            // still gets the correct Analog/Digital Y axis + colour from the channel.
            var (series, _) = factory.CreateChannelSeries(
                channel.ChannelName, channel.DeviceSerialNo, channel.Type, channel.Color, model, null);
            series.ItemsSource = channelPoints;
            model.Series.Add(series);
            legend.Add(new ViewerChannel(channel.ChannelName, channel.Color));
        }

        return model.Series.Count == 0 ? null : (model, legend);
    }

    // ---- Delete (#7 phase-2): per-session + delete-all, each behind a confirm ----

    /// <summary>Deletes one session after a destructive confirm. The DB delete is transactional and
    /// rethrows on failure (<see cref="SessionDataRepository.DeleteSession"/>), so the bound row is
    /// removed only on success — a failed delete keeps the row rather than hiding data that survived.</summary>
    [RelayCommand]
    private async Task DeleteSession(LoggingSession? session)
    {
        // Also bail if a confirm is already open, so a second destructive prompt can't be stacked
        // (e.g. via keyboard/switch-access reaching a delete button behind the scrim).
        if (session is null || IsBusy || ConfirmOverlay.IsOpen) { return; }

        var confirmed = await ConfirmOverlay.ShowAsync(
            "Delete session?",
            $"Delete “{session.Name}”? This permanently removes its logged data and can't be undone.",
            affirmativeLabel: "DELETE", isDestructive: true);
        if (!confirmed) { return; }

        // Re-verify no logging is active before touching storage — it may have started while the
        // confirm was open, and a delete must not race an in-flight logging session's writes.
        if (LoggingManager.Instance.Active)
        {
            StatusMessage = "Stop logging before deleting a session.";
            return;
        }

        var repo = Repo();
        if (repo is null) { StatusMessage = "Delete isn't available on this device."; return; }

        IsBusy = true;
        try
        {
            await Task.Run(() => repo.DeleteSession(session));
            Sessions.Remove(session);
            StatusMessage = "Session deleted.";
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(HasNoSessions));
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Mobile: failed to delete session");
            StatusMessage = "Couldn't delete that session — see logs.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Deletes every listed session after a destructive confirm, refusing while logging is
    /// active (mirrors the desktop guard). Each delete is independent, so a single failure doesn't
    /// abort the rest; only the sessions that actually deleted are removed from the list, and the
    /// reported count is truthful.</summary>
    [RelayCommand]
    private async Task DeleteAll()
    {
        // Also bail if a confirm is already open, so overlapping destructive prompts can't stack.
        if (IsBusy || Sessions.Count == 0 || ConfirmOverlay.IsOpen) { return; }

        // Refuse while a logging session is writing — a bulk delete must not race active writes.
        if (LoggingManager.Instance.Active)
        {
            StatusMessage = "Stop logging before deleting all sessions.";
            return;
        }

        // Snapshot the session set BEFORE prompting so the confirmed count matches exactly what gets
        // deleted (the collection could change while the confirm is open).
        var toDelete = Sessions.ToArray();
        var total = toDelete.Length;
        var confirmed = await ConfirmOverlay.ShowAsync(
            "Delete all sessions?",
            $"Delete all {total} logged session{(total == 1 ? "" : "s")}? This permanently removes their data and can't be undone.",
            affirmativeLabel: "DELETE ALL", isDestructive: true);
        if (!confirmed) { return; }

        // Re-verify logging didn't start while the confirm was open.
        if (LoggingManager.Instance.Active)
        {
            StatusMessage = "Stop logging before deleting all sessions.";
            return;
        }

        var repo = Repo();
        if (repo is null) { StatusMessage = "Delete isn't available on this device."; return; }

        IsBusy = true;
        try
        {
            var deleted = await Task.Run(() =>
            {
                var succeeded = new List<LoggingSession>(toDelete.Length);
                foreach (var session in toDelete)
                {
                    try { repo.DeleteSession(session); succeeded.Add(session); }
                    catch (Exception ex) { _appLogger.Error(ex, $"Mobile: delete-all failed for session {session.ID}"); }
                }
                return succeeded;
            });

            foreach (var session in deleted) { Sessions.Remove(session); }
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(HasNoSessions));

            StatusMessage = deleted.Count == total
                ? $"Deleted {total} session{(total == 1 ? "" : "s")}."
                : $"Deleted {deleted.Count} of {total}; some couldn't be removed — see logs.";
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Mobile: delete-all failed");
            StatusMessage = "Delete all failed — see logs.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Lazily builds a <see cref="SessionDataRepository"/> over the mobile DI DbContext
    /// factory (the same one <c>App.InitializeMobile</c> registers and <see cref="Reload"/> reads
    /// through). Returns null if the data layer never initialized, so viewer/delete degrade to a
    /// status message instead of throwing.</summary>
    private SessionDataRepository? Repo()
    {
        if (_repository is not null) { return _repository; }

        var provider = Daqifi.Desktop.App.ServiceProvider;
        _factory = provider?.GetService<IDbContextFactory<LoggingContext>>();
        if (_factory is null) { return null; }

        _repository = new SessionDataRepository(_factory, _appLogger);
        return _repository;
    }

    /// <summary>Exports one session; returns true only if the export actually completed and
    /// the destination now holds this run's CSV — so the caller reports a truthful count and
    /// never claims "Exported…" for a failed export or clobbers a good prior file.</summary>
    /// <remarks>
    /// The staging this used to do for itself now lives in <see cref="OptimizedLoggingSessionExporter"/>
    /// (issue #236), where the desktop export gets it too: every export is written beside its
    /// destination and moved into place only once it has finished, so a failed or cancelled export
    /// leaves a good prior CSV untouched. All that is left to do here is believe the return value.
    /// The exporter is passed in (one per batch) so the CSV delimiter it captured at construction
    /// stays fixed across a whole "Export all" even if Settings is toggled.
    /// </remarks>
    /// <summary>The name the save picker suggests for a single-session export: the session's
    /// safe file name WITHOUT an extension. The picker adds <c>.csv</c> itself (the view sets
    /// <c>DefaultExtension</c> to <see cref="ExportFileNamer.CsvExtension"/>, and the Android
    /// picker appends it to this name), so adding it here as well gave <c>.csv.csv</c> (#587).</summary>
    internal static string SuggestedSaveName(string? sessionName, int sessionId) =>
        ExportFileNamer.SafeName(sessionName, sessionId);

    /// <summary>The status line after "Export all": names the file when there is one, and the
    /// folder in every case, so the user knows where to look.</summary>
    internal static string ExportAllStatus(IReadOnlyList<string> written, int total, string folderName)
    {
        if (written.Count == 0)
        {
            return $"Couldn't export to {folderName}. Nothing was saved — see logs.";
        }

        var what = written.Count == 1 ? written[0] : $"{written.Count} files";
        return written.Count == total
            ? $"Exported {total} {(total == 1 ? "session" : "sessions")} to {folderName}: {what}."
            : $"Exported {written.Count} of {total} sessions to {folderName}: {what}. The others failed — see logs.";
    }

    private static bool ExportOne(OptimizedLoggingSessionExporter exporter, LoggingSession session, string filepath, int sessionIndex, int totalSessions) =>
        exporter.TryExportLoggingSession(
            session, filepath, exportRelativeTime: false,
            new Progress<int>(), CancellationToken.None,
            sessionIndex, totalSessions);

}

/// <summary>A single entry in the session viewer's legend: the channel name and a colour swatch
/// matching its plotted line. The colour string is the same one stored with the samples and used by
/// <see cref="PlotModelFactory.CreateChannelSeries"/> (an ARGB hex like <c>#FFD32F2F</c>), so the
/// chip and the line always agree; an unparseable value falls back to grey rather than throwing.</summary>
public sealed class ViewerChannel
{
    public string Name { get; }

    public IBrush Swatch { get; }

    public ViewerChannel(string name, string colorHex)
    {
        Name = name;
        // Use ImmutableSolidColorBrush, NOT SolidColorBrush: this runs inside BuildSessionPlot on a
        // Task.Run background thread, and SolidColorBrush is an AvaloniaObject whose ctor calls
        // Dispatcher.UIThread.VerifyAccess() → throws off the UI thread, which the catch would then
        // silently mask as grey (making every legend swatch grey). ImmutableSolidColorBrush is a plain
        // immutable IBrush with no thread affinity, so it's safe to build off-thread.
        IBrush swatch;
        try { swatch = new ImmutableSolidColorBrush(Color.Parse(colorHex)); }
        catch { swatch = new ImmutableSolidColorBrush(Colors.Gray); }
        Swatch = swatch;
    }
}
