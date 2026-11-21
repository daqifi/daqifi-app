using CommunityToolkit.Mvvm.Input;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.UpdateVersion;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// The UPDATES section of the settings drawer (#346), driven through each state against a fake
/// <see cref="IAppUpdateService"/>, with the real <see cref="AppUpdateNotifier"/> behind it. No test
/// touches the network or waits on the clock: every wait goes through <see cref="Waits"/>, which a
/// test ends by hand.
/// </summary>
public class AppUpdateViewModelTests
{
    private static readonly DateTime CheckTime = new(2026, 11, 20, 14, 2, 0);

    private sealed class FakeUpdateService : IAppUpdateService
    {
        public AppUpdateSupport Support { get; set; } = AppUpdateSupport.Supported;
        public bool ApplyNeedsElevation { get; set; }

        /// <summary>The update a check finds, or <c>null</c> when this copy is current.</summary>
        public AppUpdateOffer? Offer { get; set; }

        public Exception? CheckFailure { get; set; }

        /// <summary>When set, a check does not end until the test completes it.</summary>
        public TaskCompletionSource? CheckGate { get; set; }

        /// <summary>Runs inside the download; the test reports progress or holds it there.</summary>
        public Func<IProgress<int>?, CancellationToken, Task>? DownloadBody { get; set; }

        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Applies { get; private set; }

        public async Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            if (CheckGate is { } gate) { await gate.Task; }
            return CheckFailure is null ? Offer : throw CheckFailure;
        }

        public async Task DownloadAsync(AppUpdateOffer offer, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            Downloads++;
            if (DownloadBody is not null) { await DownloadBody(progress, cancellationToken); }
        }

        public void ApplyAndRestart(AppUpdateOffer offer) => Applies++;

        /// <summary>True while another copy runs from this installation.</summary>
        public bool InUseElsewhere { get; set; }

        public bool IsInstallInUseElsewhere() => InUseElsewhere;
    }

    /// <summary>
    /// Every wait the view model and the notifier ask for. A wait ends only when the test calls
    /// <see cref="Release"/>, or when it is cancelled.
    /// </summary>
    private sealed class Waits
    {
        private readonly Lock _gate = new();
        private readonly List<(TimeSpan Interval, TaskCompletionSource Done)> _pending = [];
        private readonly List<(TimeSpan Interval, TaskCompletionSource Seen)> _watchers = [];
        private readonly List<TimeSpan> _requested = [];

        public IReadOnlyList<TimeSpan> Requested { get { lock (_gate) { return [.. _requested]; } } }

        public Task Delay(TimeSpan interval, CancellationToken token)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => done.TrySetCanceled(token));
            List<TaskCompletionSource> seen;
            lock (_gate)
            {
                _requested.Add(interval);
                _pending.Add((interval, done));
                seen = _watchers.Where(w => w.Interval == interval).Select(w => w.Seen).ToList();
                _watchers.RemoveAll(w => w.Interval == interval);
            }

            seen.ForEach(s => s.TrySetResult());
            return done.Task;
        }

        /// <summary>Completes when a wait of this length is under way. Fails after 30 s, which only a hang reaches.</summary>
        public Task Pending(TimeSpan interval)
        {
            lock (_gate)
            {
                if (_pending.Any(p => p.Interval == interval)) { return Task.CompletedTask; }
                var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _watchers.Add((interval, seen));
                return seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }

        /// <summary>Ends the oldest wait of this length.</summary>
        public void Release(TimeSpan interval)
        {
            TaskCompletionSource done;
            lock (_gate)
            {
                var index = _pending.FindIndex(p => p.Interval == interval);
                Assert.True(index >= 0, $"no wait of {interval} is under way");
                done = _pending[index].Done;
                _pending.RemoveAt(index);
            }

            done.TrySetResult();
        }
    }

    private sealed class Harness : IDisposable
    {
        public FakeUpdateService Service { get; } = new();
        public Waits Waits { get; } = new();
        public UpdateBlocker Blocker { get; set; }
        public List<Notifications?> Bell { get; } = [];
        public RecordingAppLogger Logger { get; } = new();
        public List<string> Confirms { get; } = [];
        public int SectionOpened { get; private set; }

        private AppUpdateViewModel? _viewModel;

        public AppUpdateViewModel ViewModel => _viewModel ??= new(
            Service,
            () => Blocker,
            (message, _) => { Confirms.Add(message); return Task.FromResult(true); },
            notice => Bell.Add(notice),
            () => SectionOpened++,
            Logger,
            now: () => CheckTime,
            delay: Waits.Delay);

        public Task CheckNowAsync() => ViewModel.CheckNowCommand.ExecuteAsync(null);

        /// <summary>Clicks the button the section shows for the update, without waiting for what it starts.</summary>
        public Task ClickAction() => ((IAsyncRelayCommand)ViewModel.ActionCommand!).ExecuteAsync(null);

        public void Dispose() => _viewModel?.Dispose();
    }

    [Theory]
    [InlineData("3.3.0", null, "3.3.0")]
    [InlineData("3.3.0", "9f1c2ab", "3.3.0 (9f1c2ab)")]
    [InlineData("3.4.0-beta.1", null, "3.4.0-beta.1")]
    [InlineData(null, null, "dev")]
    public void Version_IsTheSemanticVersion_WithTheCommitWhenGiven(string? semantic, string? commit, string expected) =>
        Assert.Equal(expected, AppUpdateViewModel.FormatVersion(semantic, commit));

    /// <summary>Android and iOS update through their stores: the section has the version and no update state.</summary>
    [Fact]
    public async Task PlatformThatDoesNotUpdateItself_ShowsNoUpdateState_AndNeverChecks()
    {
        using var h = new Harness();
        h.Service.Support = AppUpdateSupport.PlatformUnsupported;

        await h.ViewModel.RunAsync();
        await h.CheckNowAsync();

        Assert.False(h.ViewModel.HasStatus);
        Assert.False(h.ViewModel.CanCheckNow);
        Assert.Equal(0, h.Service.Checks);
        Assert.Empty(h.Waits.Requested);
    }

    [Theory]
    [InlineData(AppUpdateSupport.NotInstalled, "not installed with the DAQiFi installer")]
    [InlineData(AppUpdateSupport.OwnedExternally, "from the store it came from")]
    [InlineData(AppUpdateSupport.DisabledByPolicy, "An administrator turned off updates")]
    public async Task CopyThatCannotUpdate_SaysWhy_AndNeverChecks(AppUpdateSupport support, string reason)
    {
        using var h = new Harness();
        h.Service.Support = support;

        await h.ViewModel.RunAsync();
        await h.CheckNowAsync();

        Assert.True(h.ViewModel.HasStatus);
        Assert.Contains(reason, h.ViewModel.Status);
        Assert.False(h.ViewModel.CanCheckNow);
        Assert.Equal(0, h.Service.Checks);
        Assert.Empty(h.Waits.Requested);
    }

    [Fact]
    public void BeforeTheFirstCheck_SaysSo_AndOffersCheckNow()
    {
        using var h = new Harness();

        Assert.True(h.ViewModel.HasStatus);
        Assert.Equal("Not checked yet.", h.ViewModel.Status);
        Assert.True(h.ViewModel.CanCheckNow);
        Assert.True(h.ViewModel.CheckNowCommand.CanExecute(null));
        Assert.False(h.ViewModel.HasAction);
    }

    /// <summary>The first check waits, so that it does not run at the same time as device discovery.</summary>
    [Fact]
    public async Task FirstCheck_WaitsForTheStartupDelay()
    {
        using var h = new Harness();

        var run = h.ViewModel.RunAsync();

        Assert.Equal([AppUpdateViewModel.StartupDelay], h.Waits.Requested);
        Assert.Equal(0, h.Service.Checks);

        h.Waits.Release(AppUpdateViewModel.StartupDelay);
        await h.Waits.Pending(AppUpdateViewModel.CheckInterval);

        Assert.Equal(1, h.Service.Checks);
        Assert.False(run.IsCompleted);
    }

    [Fact]
    public async Task WhileChecking_SaysSo_AndCheckNowCannotRunASecondCheck()
    {
        using var h = new Harness();
        h.Service.CheckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var check = h.CheckNowAsync();

        Assert.True(h.ViewModel.IsChecking);
        Assert.Equal("Checking for updates...", h.ViewModel.Status);
        Assert.False(h.ViewModel.CheckNowCommand.CanExecute(null));

        h.Service.CheckGate.SetResult();
        await check;

        Assert.False(h.ViewModel.IsChecking);
        Assert.True(h.ViewModel.CheckNowCommand.CanExecute(null));
        Assert.Equal(1, h.Service.Checks);
    }

    [Fact]
    public async Task CurrentCopy_SaysUpToDate_WithTheTimeOfTheCheck()
    {
        using var h = new Harness();

        await h.CheckNowAsync();

        Assert.Equal($"Up to date. Checked at {CheckTime:t}.", h.ViewModel.Status);
        Assert.True(h.ViewModel.CanCheckNow);
        Assert.Empty(h.Bell);
    }

    /// <summary>No network is normal for a bench PC: one quiet line and a warning in the log, never an error or a notification.</summary>
    [Fact]
    public async Task FailedCheck_IsAQuietLine_UntilACheckWorks()
    {
        using var h = new Harness();
        h.Service.CheckFailure = new HttpRequestException("offline");

        await h.CheckNowAsync();

        Assert.Equal("Couldn't check for updates.", h.ViewModel.Status);
        Assert.True(h.ViewModel.CanCheckNow);
        Assert.Single(h.Logger.Warnings);
        Assert.Empty(h.Logger.Errors);
        Assert.Empty(h.Bell);

        h.Service.CheckFailure = null;
        await h.CheckNowAsync();

        Assert.Equal($"Up to date. Checked at {CheckTime:t}.", h.ViewModel.Status);
    }

    /// <summary>
    /// One source of truth: the drawer shows the notifier's notification, and the bell shows its
    /// text. Nothing is downloaded until the button is clicked (#341). "Check now" stays beside the
    /// offer, so a newer version can still be found (#346 review).
    /// </summary>
    [Fact]
    public async Task FoundUpdate_DrawerShowsTheOffer_AndTheBellItsText()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);

        await h.CheckNowAsync();

        Assert.Equal("DAQiFi 3.4.0 is available.", h.ViewModel.Status);
        Assert.Equal(h.ViewModel.Notice!.Message, Assert.Single(h.Bell)!.Message);
        Assert.Equal("Download update", h.ViewModel.ActionText);
        Assert.True(h.ViewModel.HasAction);
        Assert.True(h.ViewModel.CanCheckNow);
        Assert.Equal(0, h.Service.Downloads);
    }

    /// <summary>The timer's check and a "Check now" click can come together; only one check runs.</summary>
    [Fact]
    public async Task TimedCheck_WhileACheckRuns_DoesNotStartASecondOne()
    {
        using var h = new Harness();
        h.Service.CheckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = h.CheckNowAsync();

        _ = h.ViewModel.RunAsync();
        h.Waits.Release(AppUpdateViewModel.StartupDelay);
        await h.Waits.Pending(AppUpdateViewModel.CheckInterval);

        Assert.Equal(1, h.Service.Checks);

        h.Service.CheckGate.SetResult();
        await check;
    }

    [Fact]
    public async Task DownloadFromTheDrawer_ShowsItsProgress_ThenReady()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.DownloadBody = async (progress, _) =>
        {
            progress!.Report(42);
            await downloading.Task;
        };
        await h.CheckNowAsync();

        var download = h.ClickAction();

        Assert.Equal("Downloading DAQiFi 3.4.0... 42%", h.ViewModel.Status);
        Assert.Equal("Cancel", h.ViewModel.ActionText);
        Assert.Equal(h.ViewModel.Status, h.Bell.Last()!.Message);

        downloading.SetResult();
        await download;

        Assert.Equal("DAQiFi 3.4.0 is ready.", h.ViewModel.Status);
        Assert.Equal(0, h.Service.Applies);
    }

    [Fact]
    public async Task CancelFromTheDrawer_OffersTheUpdateAgain()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        h.Service.DownloadBody = (_, token) => Task.Delay(Timeout.Infinite, token);
        await h.CheckNowAsync();
        var install = h.ClickAction();

        ((IRelayCommand)h.ViewModel.ActionCommand!).Execute(null); // Cancel
        await install;

        Assert.Equal("DAQiFi 3.4.0 is available.", h.ViewModel.Status);
        Assert.Equal("Download update", h.ViewModel.ActionText);
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>Downloading stops nothing, so the offer keeps its button while logging runs. Only the restart waits.</summary>
    [Fact]
    public async Task WhileLogging_TheOfferStillDownloads()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        h.Blocker = UpdateBlocker.Logging;

        await h.CheckNowAsync();

        Assert.Equal("DAQiFi 3.4.0 is available.", h.ViewModel.Status);
        Assert.Equal("Download update", h.ViewModel.ActionText);
        Assert.True(h.ViewModel.CanCheckNow);
    }

    /// <summary>This app stays open for hours: it checks again on a timer.</summary>
    [Fact]
    public async Task NoUpdate_ChecksAgainAfterTheInterval()
    {
        using var h = new Harness();
        _ = h.ViewModel.RunAsync();
        h.Waits.Release(AppUpdateViewModel.StartupDelay);
        await h.Waits.Pending(AppUpdateViewModel.CheckInterval);
        Assert.Equal(1, h.Service.Checks);

        h.Waits.Release(AppUpdateViewModel.CheckInterval);
        await h.Waits.Pending(AppUpdateViewModel.CheckInterval);

        Assert.Equal(2, h.Service.Checks);
        Assert.Equal($"Up to date. Checked at {CheckTime:t}.", h.ViewModel.Status);
    }

    [Fact]
    public async Task Dispose_StopsTheChecks()
    {
        using var h = new Harness();
        var run = h.ViewModel.RunAsync();

        h.ViewModel.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, h.Service.Checks);
    }

    // ---- PR #661 review, thread 1: an offer is refreshed while the app stays open ----

    /// <summary>A bench PC stays open across releases: a newer version replaces the one on offer, in the drawer and in the bell.</summary>
    [Fact]
    public async Task NewerVersion_ReplacesTheOffer_OnCheckNow()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();

        h.Service.Offer = new("3.4.1", null);
        await h.CheckNowAsync();

        Assert.Equal(2, h.Service.Checks);
        Assert.Equal("DAQiFi 3.4.1 is available.", h.ViewModel.Status);
        Assert.Equal("DAQiFi 3.4.1 is available.", h.Bell.Last()!.Message);
    }

    /// <summary>A version taken off the feed is no longer offered: both surfaces clear.</summary>
    [Fact]
    public async Task WithdrawnVersion_ClearsTheDrawerAndTheBell()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();

        h.Service.Offer = null;
        await h.CheckNowAsync();

        Assert.Null(h.ViewModel.Notice);
        Assert.Null(h.Bell.Last());
        Assert.Equal($"Up to date. Checked at {CheckTime:t}.", h.ViewModel.Status);
    }

    /// <summary>The timer goes on after an offer, so a newer release reaches an app that stays open.</summary>
    [Fact]
    public async Task TimedChecks_GoOnAfterAnOffer()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        _ = h.ViewModel.RunAsync();
        h.Waits.Release(AppUpdateViewModel.StartupDelay);
        await h.Waits.Pending(AppUpdateViewModel.CheckInterval);

        h.Service.Offer = new("3.4.1", null);
        h.Waits.Release(AppUpdateViewModel.CheckInterval);
        await h.Waits.Pending(AppUpdateViewModel.CheckInterval);

        Assert.Equal(2, h.Service.Checks);
        Assert.Equal("DAQiFi 3.4.1 is available.", h.ViewModel.Status);
    }

    /// <summary>No check runs while a download is under way: a new offer would replace its progress.</summary>
    [Fact]
    public async Task DuringADownload_NoCheckRuns()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        h.Service.DownloadBody = (_, token) => Task.Delay(Timeout.Infinite, token);
        await h.CheckNowAsync();
        _ = h.ClickAction();

        await h.CheckNowAsync();

        Assert.Equal(1, h.Service.Checks);
        Assert.False(h.ViewModel.CanCheckNow);
        Assert.StartsWith("Downloading DAQiFi 3.4.0", h.ViewModel.Status);
    }

    // ---- PR #661 review, thread 3: download first, then a separate restart ----

    /// <summary>A finished download does not restart the app. "Restart to update" does, when the user chooses.</summary>
    [Fact]
    public async Task FinishedDownload_WaitsForRestartToUpdate()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();

        await h.ClickAction().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, h.Service.Downloads);
        Assert.Equal(0, h.Service.Applies);
        Assert.Equal("DAQiFi 3.4.0 is ready.", h.ViewModel.Status);
        Assert.Equal("Restart to update", h.ViewModel.ActionText);

        await h.ClickAction().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>
    /// Downloading stops nothing, so it starts while logging runs. The restart would stop the
    /// session (#348, #653), so "Restart to update" has no button until logging stops.
    /// </summary>
    [Fact]
    public async Task RestartToUpdate_WhileLogging_HasNoButton_UntilLoggingStops()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();
        h.Blocker = UpdateBlocker.Logging;

        await h.ClickAction().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, h.Service.Downloads);
        Assert.Equal("DAQiFi 3.4.0 is ready. Restart when logging stops.", h.ViewModel.Status);
        Assert.False(h.ViewModel.HasAction);

        h.Blocker = UpdateBlocker.None;
        h.Waits.Release(AppUpdateNotifier.OfferRecheckInterval);
        await h.Waits.Pending(AppUpdateNotifier.OfferRecheckInterval);

        Assert.Equal("DAQiFi 3.4.0 is ready.", h.ViewModel.Status);
        Assert.Equal("Restart to update", h.ViewModel.ActionText);
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>Installing closes every copy of DAQiFi on the PC, so a click while another runs restarts nothing and says why.</summary>
    [Fact]
    public async Task RestartToUpdate_WhileAnotherCopyRuns_RestartsNothing_AndSaysWhy()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();
        await h.ClickAction().WaitAsync(TimeSpan.FromSeconds(30));
        h.Service.InUseElsewhere = true;

        await h.ClickAction().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, h.Service.Applies);
        Assert.Equal("DAQiFi 3.4.0 is ready. Restart when other copies of DAQiFi on this PC are closed.", h.ViewModel.Status);
        Assert.Equal("Restart to update", h.ViewModel.ActionText);
    }

    /// <summary>On an install for all users, the administrator question comes with the restart, not with the download.</summary>
    [Fact]
    public async Task AllUsersInstall_AsksAtTheRestart_NotAtTheDownload()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        h.Service.ApplyNeedsElevation = true;
        await h.CheckNowAsync();

        await h.ClickAction().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(h.Confirms);

        await h.ClickAction().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("administrator", Assert.Single(h.Confirms));
        Assert.Equal(1, h.Service.Applies);
    }

    // ---- PR #661 review, thread 2: the bell entry opens the UPDATES section ----

    /// <summary>The bell says what the drawer says, and its button opens the drawer. It starts no download.</summary>
    [Fact]
    public async Task BellEntry_OpensTheSettings_AndStartsNoDownload()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();

        var entry = h.Bell.Last()!;
        Assert.Equal(h.ViewModel.Status, entry.Message);
        Assert.Equal("Open in Settings", entry.ActionText);

        entry.Action!.Execute(null);

        Assert.Equal(1, h.SectionOpened);
        Assert.Equal(0, h.Service.Downloads);
        Assert.Equal("Download update", h.ViewModel.ActionText);   // the drawer keeps its own button
    }

    /// <summary>Every state the bell shows opens the section, the download's progress included.</summary>
    [Fact]
    public async Task BellEntry_DuringADownload_StillOpensTheSettings()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        h.Service.DownloadBody = (_, token) => Task.Delay(Timeout.Infinite, token);
        await h.CheckNowAsync();
        _ = h.ClickAction();

        var entry = h.Bell.Last()!;
        Assert.StartsWith("Downloading DAQiFi 3.4.0", entry.Message);
        entry.Action!.Execute(null);

        Assert.Equal(1, h.SectionOpened);
        Assert.Equal("Cancel", h.ViewModel.ActionText);              // the bell did not cancel it
    }

    // ---- PR #661 review, round 2 ----

    /// <summary>
    /// A refresh is under way when the user clicks Download. The click must not start a download of
    /// the version the refresh may replace: when the refresh returns 3.4.1, a finished download of
    /// 3.4.0 would show "3.4.0 is ready", and its restart would fail (round 2, thread 1).
    /// </summary>
    [Fact]
    public async Task DownloadClickedDuringARefresh_DownloadsNothing_ThenTheNewOffer()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.DownloadBody = (_, _) => downloading.Task;

        h.Service.CheckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.Offer = new("3.4.1", null);
        var refresh = h.CheckNowAsync();
        Assert.False(h.ViewModel.ActionCommand!.CanExecute(null));
        var click = h.ClickAction();

        h.Service.CheckGate.SetResult();
        await refresh;
        downloading.SetResult();
        await click.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, h.Service.Downloads);
        Assert.Equal("DAQiFi 3.4.1 is available.", h.ViewModel.Status);

        h.Service.CheckGate = null;
        await h.ClickAction().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, h.Service.Downloads);
        Assert.Equal("DAQiFi 3.4.1 is ready.", h.ViewModel.Status);
    }

    /// <summary>The button greys out when a refresh starts and comes back when it ends: the view hears both changes.</summary>
    [Fact]
    public async Task Refresh_TellsTheButtonWhenItStartsAndEnds()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();
        var command = h.ViewModel.ActionCommand!;
        var changes = new List<bool>();
        command.CanExecuteChanged += (_, _) => changes.Add(command.CanExecute(null));

        await h.CheckNowAsync();

        Assert.Equal([false, true], changes);
    }

    /// <summary>"Restart to update" is held during a refresh as well, which can replace the ready version.</summary>
    [Fact]
    public async Task RestartClickedDuringARefresh_RestartsNothing()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();
        await h.ClickAction().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("Restart to update", h.ViewModel.ActionText);

        h.Service.CheckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = h.CheckNowAsync();
        await h.ClickAction().WaitAsync(TimeSpan.FromSeconds(30));
        h.Service.CheckGate.SetResult();
        await refresh;

        Assert.Equal(0, h.Service.Applies);
        Assert.True(h.ViewModel.ActionCommand!.CanExecute(null));
    }

    /// <summary>
    /// Offline, a refresh over an offer says so, and the offer stays (round 2, thread 2). The line
    /// goes back to the plain offer when a check works, and when the user moves the update on.
    /// </summary>
    [Fact]
    public async Task FailedRefresh_OverAnOffer_SaysSo_AndKeepsTheOffer()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();

        h.Service.CheckFailure = new HttpRequestException("offline");
        await h.CheckNowAsync();

        Assert.Equal("Couldn't check for updates. DAQiFi 3.4.0 is available.", h.ViewModel.Status);
        Assert.Equal("Download update", h.ViewModel.ActionText);
        Assert.Equal("DAQiFi 3.4.0 is available.", h.Bell.Last()!.Message);

        h.Service.CheckFailure = null;
        await h.CheckNowAsync();

        Assert.Equal("DAQiFi 3.4.0 is available.", h.ViewModel.Status);
    }

    /// <summary>The view reads the failure line when it is told: the last Status change after a failed refresh carries it.</summary>
    [Fact]
    public async Task FailedRefresh_TellsTheViewTheNewLine()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();
        var lines = new List<string>();
        h.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppUpdateViewModel.Status)) { lines.Add(h.ViewModel.Status); }
        };

        h.Service.CheckFailure = new HttpRequestException("offline");
        await h.CheckNowAsync();

        Assert.Equal("Couldn't check for updates. DAQiFi 3.4.0 is available.", lines.Last());
    }

    /// <summary>The failure belongs to the offer it was checked over: a download that starts after it shows its own line.</summary>
    [Fact]
    public async Task FailedRefresh_ThenDownload_ShowsTheDownloadAlone()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        await h.CheckNowAsync();
        h.Service.CheckFailure = new HttpRequestException("offline");
        await h.CheckNowAsync();

        await h.ClickAction().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("DAQiFi 3.4.0 is ready.", h.ViewModel.Status);
    }

    // ---- PR #661 review, thread 4: the short build ID in every build ----

    /// <summary>
    /// The row shows the short commit in every configuration. A Debug test run cannot see a Release
    /// difference, so this pins the formula; the Release capture (desktop-6) shows the result.
    /// </summary>
    [Fact]
    public void Version_HasTheShortBuildId_WhenTheBuildHasOne()
    {
        using var h = new Harness();

        Assert.Equal(
            AppUpdateViewModel.FormatVersion(Daqifi.Desktop.Common.AppVersion.Semantic, Daqifi.Desktop.Common.AppVersion.ShortBuildMetadata),
            h.ViewModel.Version);
    }
}
