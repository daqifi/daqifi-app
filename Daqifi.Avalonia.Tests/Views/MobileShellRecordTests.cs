using System.Reflection;
using Avalonia.Platform.Storage;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Avalonia.Views;
using Daqifi.Avalonia.Views.Mobile;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;
using CoreStreamingDevice = Daqifi.Core.Device.DaqifiStreamingDevice;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #597: the phone could stream but not record, while APP LOGS said that streamed sessions
/// appear there. The shell now has a Record switch, and a recording lasts exactly as long as its
/// stream. These tests drive the shell's real stream commands against the app's device wrapper
/// over a real Core device; only the recorder is replaced, because the real one needs
/// <c>LoggingManager.Instance</c>, which the test project cannot construct (no app host).
/// </summary>
/// <remarks>
/// In the <c>ConnectionManager</c> singleton collection because the shell's adopt step registers
/// the device with <c>ConnectionManager.Instance</c>.
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class MobileShellRecordTests : IDisposable
{
    private const int AnalogPorts = 16;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly CapturingTransport _transport = new();
    private readonly CoreStreamingDevice _core;
    private readonly ShellTestDevice _device;
    private readonly MobileShellViewModel _shell = new();
    private readonly FakeRecorder _recorder;
    private readonly SerialTestUi _ui = new();

    public MobileShellRecordTests()
    {
        _core = new CoreStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _core.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = AnalogPorts,
            AnalogInRes = 65535
        });

        _device = new ShellTestDevice("Record test device");
        _device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };
        _device.AttachCore(_core);
        _device.SyncFromCore(_core);

        _recorder = new FakeRecorder(_transport);
        _shell.Recorder = _recorder;
        // No dispatcher runs in this project; the outcome banner is posted through this seam.
        _shell.PostToUiThread = action => action();
        _device.AdoptInto(_shell);
        Assert.True(_shell.IsConnected);
    }

    public void Dispose()
    {
        _shell.Dispose();
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();
        _ui.Dispose();
    }

    [Fact]
    public void Without_the_switch_a_stream_is_not_recorded()
    {
        Assert.Equal("Start streaming", _shell.StreamButtonText);
        Assert.False(_shell.ShowRecordStyle);

        StartStream();

        Assert.False(_shell.IsRecording);
        Assert.Equal(0, _recorder.Starts);
        Assert.Equal("Stop streaming", _shell.StreamButtonText);

        _shell.StreamToggleCommand.Execute(null);
        Assert.Equal(0, _recorder.Stops);
        Assert.False(_shell.HasRecordingOutcome);
    }

    [Fact]
    public void The_switch_makes_the_button_say_that_the_tap_records()
    {
        _shell.RecordStream = true;

        Assert.Equal("Start recording", _shell.StreamButtonText);
        Assert.True(_shell.ShowRecordStyle);
    }

    [Fact]
    public void A_recording_opens_before_the_device_streams_and_covers_every_streamed_channel()
    {
        _shell.RecordStream = true;

        StartStream();

        Assert.True(_shell.IsRecording);
        Assert.Equal(1, _recorder.Starts);
        Assert.False(
            _recorder.DeviceWasStreamingAtStart,
            "The session opened after the device was told to stream, so the first samples were not recorded.");
        Assert.Equal(AnalogPorts, _recorder.Channels.Count);
        Assert.All(_recorder.Channels, channel => Assert.True(channel.IsActive));
        Assert.Equal("Stop recording", _shell.StreamButtonText);
        Assert.True(_shell.ShowRecordStyle);
        Assert.StartsWith("Recording 16 channel(s)", _shell.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// PR #609 review: "saved" must wait for the samples to be committed to SQLite, because APP LOGS
    /// lists only committed sessions. Until the recorder confirms, the banner says "saving".
    /// </summary>
    [Fact]
    public async Task Stop_says_saving_until_the_samples_are_committed_then_names_the_session()
    {
        _shell.RecordStream = true;
        StartStream();

        _shell.StreamToggleCommand.Execute(null);

        Assert.False(_shell.IsStreaming);
        Assert.False(_shell.IsRecording);
        Assert.Equal(1, _recorder.Stops);
        Assert.True(_shell.IsSavingRecording);
        Assert.False(_shell.RecordingSaved);
        Assert.False(_shell.ShowRecordingWarning);
        Assert.Equal("Saving the recording…", _shell.RecordingOutcome);

        _recorder.Complete(new RecordingResult("Session_7", 1_600, null));
        await _shell.RecordingSaveTask.WaitAsync(SendTimeout);

        Assert.False(_shell.IsSavingRecording);
        Assert.True(_shell.RecordingSaved);
        Assert.Equal(
            $"Session_7 saved: {1_600:N0} samples. Open Logged Data > APP LOGS to see it.",
            _shell.RecordingOutcome);
    }

    [Fact]
    public async Task A_recording_that_kept_no_samples_says_so()
    {
        _shell.RecordStream = true;
        StartStream();

        _shell.StreamToggleCommand.Execute(null);
        _recorder.Complete(new RecordingResult("Session_7", 0, null));
        await _shell.RecordingSaveTask.WaitAsync(SendTimeout);

        Assert.False(_shell.RecordingSaved);
        Assert.True(_shell.ShowRecordingWarning);
        Assert.Equal("No samples arrived, so no session was saved.", _shell.RecordingOutcome);
    }

    [Fact]
    public async Task A_save_that_cannot_be_confirmed_is_never_reported_as_saved()
    {
        _shell.RecordStream = true;
        StartStream();

        _shell.StreamToggleCommand.Execute(null);
        _recorder.Complete(new RecordingResult("Session_7", 0, "the samples are not written to the database yet"));
        await _shell.RecordingSaveTask.WaitAsync(SendTimeout);

        Assert.False(_shell.RecordingSaved);
        Assert.True(_shell.ShowRecordingWarning);
        Assert.StartsWith("The recording could not be confirmed as saved", _shell.RecordingOutcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_late_confirmation_does_not_overwrite_the_next_stream()
    {
        _shell.RecordStream = true;
        StartStream();
        _shell.StreamToggleCommand.Execute(null);   // the save is still being confirmed

        _shell.RecordStream = false;
        StartStream();
        _recorder.Complete(new RecordingResult("Session_7", 1_600, null));
        await _shell.RecordingSaveTask.WaitAsync(SendTimeout);

        Assert.False(_shell.HasRecordingOutcome);
        Assert.False(_shell.IsSavingRecording);
    }

    /// <summary>
    /// Review of PR #609: the user stops a recording and opens Logged Data while the banner says
    /// "Saving the recording…". That visit reads its APP LOGS snapshot before the writer commits, so
    /// the list did not show the recording until a manual Refresh or another visit. When the save is
    /// confirmed the list must re-read in place, and a session viewer that the user opened meanwhile
    /// must stay open. Wired here exactly as <c>StorageMobileView</c> wires it.
    /// </summary>
    [Fact]
    public async Task APP_LOGS_opened_before_the_commit_shows_the_recording_once_it_is_saved()
    {
        var database = new CommittedSessions(new LoggingSession(6, "Session_6"));
        var appLogs = await OpenAppLogsAsync(database);
        Action refresh = appLogs.RefreshSessions;
        MobileShellViewModel.RecordingCommitted += refresh;
        try
        {
            _shell.RecordStream = true;
            StartStream();
            _shell.StreamToggleCommand.Execute(null);
            Assert.True(_shell.IsSavingRecording, "Arrangement: the save must still be pending.");
            // As in the app, the shell raises the commit on the UI thread.
            _shell.PostToUiThread = _ui.Post;

            // The user enters APP LOGS before the writer commits: the visit's snapshot omits it.
            await _ui.RunAsync(() => appLogs.ReloadCommand.ExecuteAsync(null));
            Assert.Equal(["Session_6"], await ListedAsync(appLogs));
            // ...and opens the older session's viewer while the save is still pending.
            await _ui.Run(() => appLogs.IsViewerOpen = true);

            database.Commit(new LoggingSession(7, "Session_7"));
            _recorder.Complete(new RecordingResult("Session_7", 1_600, null));
            await _shell.RecordingSaveTask.WaitAsync(SendTimeout);

            await WaitUntilListedAsync(appLogs, "Session_7");
            Assert.True(
                await _ui.Get(() => appLogs.IsViewerOpen),
                "The refresh closed the session viewer the user had open.");
        }
        finally
        {
            MobileShellViewModel.RecordingCommitted -= refresh;
        }
    }

    /// <summary>
    /// The same review, at its narrowest: the commit lands while the visit's own load is still
    /// running, and that load read its snapshot before the commit. The refresh must not be dropped
    /// because the list is busy; it runs once the load ends.
    /// </summary>
    [Fact]
    public async Task A_commit_during_the_visits_load_still_reaches_the_list()
    {
        var database = new CommittedSessions();
        var appLogs = await OpenAppLogsAsync(database);
        Action refresh = appLogs.RefreshSessions;
        MobileShellViewModel.RecordingCommitted += refresh;
        try
        {
            _shell.RecordStream = true;
            StartStream();
            _shell.StreamToggleCommand.Execute(null);
            _shell.PostToUiThread = _ui.Post;

            // The visit's load reads the pre-commit snapshot, then is held until after the commit.
            using var release = new SemaphoreSlim(0);
            database.HoldNextRead(release);
            var visit = await _ui.Get(() => appLogs.ReloadCommand.ExecuteAsync(null));
            Assert.True(database.WaitForHeldRead(SendTimeout), "Arrangement: the visit's load never ran.");

            database.Commit(new LoggingSession(7, "Session_7"));
            _recorder.Complete(new RecordingResult("Session_7", 1_600, null));
            await _shell.RecordingSaveTask.WaitAsync(SendTimeout);

            release.Release();
            await visit.WaitAsync(SendTimeout);
            await WaitUntilListedAsync(appLogs, "Session_7");
        }
        finally
        {
            MobileShellViewModel.RecordingCommitted -= refresh;
        }
    }

    /// <summary>
    /// The production half of the wiring the two tests above make by hand. Read off the source:
    /// <c>StorageMobileView</c> builds a view model over the real database, which this project
    /// cannot construct. It must re-read in place (<c>RefreshSessions</c>), not through
    /// <c>Refresh()</c>, which closes an open session viewer.
    /// </summary>
    [Fact]
    public void The_Storage_pane_re_reads_APP_LOGS_when_a_recording_is_committed()
    {
        var host = BindingFacts.CodeOnly(BindingFacts.Source("Daqifi.Avalonia/Views/Mobile/StorageMobileView.axaml.cs"));

        Assert.Contains("MobileShellViewModel.RecordingCommitted += OnRecordingCommitted;", host, StringComparison.Ordinal);
        Assert.Contains("MobileShellViewModel.RecordingCommitted -= OnRecordingCommitted;", host, StringComparison.Ordinal);
        Assert.Contains("vm.RefreshSessions();", host, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review of PR #609: a recording can commit while the APP LOGS export picker is open. The
    /// refresh is queued and runs just after the export writes its result. It used to clear the
    /// status line on success, so the user saw no export error at all. Here the export really
    /// fails: the listed row has no samples in memory and this project has no database, so the
    /// real exporter returns false.
    /// </summary>
    [Fact]
    public async Task A_commit_during_a_failed_export_keeps_the_export_error()
    {
        var database = new CommittedSessions(new LoggingSession(6, "Session_6"));
        var appLogs = await OpenAppLogsAsync(database);
        var directory = Directory.CreateTempSubdirectory("daqifi-applogs-export-");
        try
        {
            var picker = new TaskCompletionSource<IStorageFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _ui.Run(() => appLogs.SaveFileResolver = _ => picker.Task);

            var export = await _ui.Get(() => appLogs.ExportSessionCommand.ExecuteAsync(appLogs.Sessions[0]));
            Assert.True(await _ui.Get(() => appLogs.IsBusy), "Arrangement: the export must hold the list busy.");
            database.Commit(new LoggingSession(7, "Session_7"));
            await _ui.Run(appLogs.RefreshSessions);

            picker.SetResult(LocalStorageFile.Create(Path.Combine(directory.FullName, "Session_6.csv")));
            await export.WaitAsync(SendTimeout);
            await WaitUntilListedAsync(appLogs, "Session_7");

            Assert.Equal("Couldn't export to Session_6.csv. Nothing was saved — see logs.",
                await _ui.Get(() => appLogs.StatusMessage));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The same review, for a successful export: the queued refresh must not erase its
    /// confirmation. The row carries one sample in memory, so the real exporter writes the CSV.
    /// </summary>
    [Fact]
    public async Task A_commit_during_a_successful_export_keeps_the_confirmation()
    {
        var database = new CommittedSessions(SessionWithOneSample(6, "Session_6"));
        var appLogs = await OpenAppLogsAsync(database);
        var directory = Directory.CreateTempSubdirectory("daqifi-applogs-export-");
        try
        {
            var picker = new TaskCompletionSource<IStorageFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _ui.Run(() => appLogs.SaveFileResolver = _ => picker.Task);
            var destination = Path.Combine(directory.FullName, "Session_6.csv");

            var export = await _ui.Get(() => appLogs.ExportSessionCommand.ExecuteAsync(appLogs.Sessions[0]));
            Assert.True(await _ui.Get(() => appLogs.IsBusy), "Arrangement: the export must hold the list busy.");
            database.Commit(new LoggingSession(7, "Session_7"));
            await _ui.Run(appLogs.RefreshSessions);

            picker.SetResult(LocalStorageFile.Create(destination));
            await export.WaitAsync(SendTimeout);
            await WaitUntilListedAsync(appLogs, "Session_7");

            Assert.True(new FileInfo(destination) is { Exists: true, Length: > 0 }, "Arrangement: the export wrote no CSV.");
            Assert.Equal("Exported 1 session to Session_6.csv.", await _ui.Get(() => appLogs.StatusMessage));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The same review, for a failed delete. <c>DeleteSession</c> itself is out of reach here (it
    /// reads <c>LoggingManager.Instance.Active</c>, which this project cannot construct), so the
    /// test plays its part: it holds the list busy while the delete runs, writes the line its
    /// catch writes, then releases the list.
    /// </summary>
    [Fact]
    public async Task A_commit_during_a_failed_delete_keeps_the_delete_error()
    {
        const string deleteFailed = "Couldn't delete that session — see logs.";
        var database = new CommittedSessions(new LoggingSession(6, "Session_6"));
        var appLogs = await OpenAppLogsAsync(database);

        await _ui.Run(() =>
        {
            appLogs.IsBusy = true;
            database.Commit(new LoggingSession(7, "Session_7"));
            appLogs.RefreshSessions();
            Assert.DoesNotContain(appLogs.Sessions, s => s.Name == "Session_7");
            appLogs.StatusMessage = deleteFailed;
            appLogs.IsBusy = false;
        });

        await WaitUntilListedAsync(appLogs, "Session_7");
        Assert.Equal(deleteFailed, await _ui.Get(() => appLogs.StatusMessage));
    }

    /// <summary>
    /// The other half of that review: the refresh keeps an operation result, but it still clears
    /// a load error once a load succeeds, so the list never shows "Couldn't load…" over its rows.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_loads_clears_an_earlier_load_error()
    {
        var database = new CommittedSessions(new LoggingSession(6, "Session_6"));
        var appLogs = await OpenAppLogsAsync(database);

        database.FailNextRead();
        await _ui.RunAsync(() => appLogs.ReloadCommand.ExecuteAsync(null));
        var (failed, message) = await _ui.Get(() => (appLogs.LoadFailed, appLogs.StatusMessage));
        Assert.True(failed, "Arrangement: the visit's load must fail.");
        Assert.NotEqual(string.Empty, message);

        database.Commit(new LoggingSession(7, "Session_7"));
        await _ui.Run(appLogs.RefreshSessions);
        await WaitUntilListedAsync(appLogs, "Session_7");

        (failed, message) = await _ui.Get(() => (appLogs.LoadFailed, appLogs.StatusMessage));
        Assert.False(failed);
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void A_recording_that_cannot_start_streams_nothing()
    {
        _shell.RecordStream = true;
        _recorder.StartFailure = "Recording could not start.";

        _shell.StreamToggleCommand.Execute(null);

        Assert.False(_shell.IsStreaming);
        Assert.False(_shell.IsRecording);
        Assert.Equal("Recording could not start.", _shell.Status);
        Assert.DoesNotContain("SYSTem:StartStreamData", _transport.SentText, StringComparison.Ordinal);
        Assert.Equal(0, _recorder.Stops);
    }

    /// <summary>
    /// The lost-connection path (reached from the silent-stream watchdog and from the device's own
    /// IsConnected) closes the recording. Driven straight through <c>HandleConnectionLost</c>, not
    /// by counting watchdog polls: how the watchdog measures silence is #604's business, and this
    /// test must not depend on it.
    /// </summary>
    [Fact]
    public void A_lost_connection_closes_the_recording()
    {
        _shell.RecordStream = true;
        StartStream();

        var lost = typeof(MobileShellViewModel).GetMethod(
            "HandleConnectionLost", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(lost);
        lost.Invoke(_shell, null);

        Assert.False(_shell.IsConnected);
        Assert.False(_shell.IsRecording);
        Assert.Equal(1, _recorder.Stops);
        Assert.True(_shell.HasRecordingOutcome, "The user must still see what the recording kept.");
    }

    [Fact]
    public void Leaving_the_screen_closes_the_recording()
    {
        _shell.RecordStream = true;
        StartStream();

        _shell.Dispose();

        Assert.False(_shell.IsRecording);
        Assert.Equal(1, _recorder.Stops);
    }

    [Fact]
    public void The_next_stream_clears_the_last_recording_outcome()
    {
        _shell.RecordStream = true;
        StartStream();
        _shell.StreamToggleCommand.Execute(null);
        Assert.True(_shell.HasRecordingOutcome);

        _shell.RecordStream = false;
        StartStream();

        Assert.False(_shell.HasRecordingOutcome);
    }

    [Theory]
    [InlineData("IsChecked=\"{Binding RecordStream}\"")]
    [InlineData("Classes.record=\"{Binding ShowRecordStyle}\"")]
    [InlineData("Text=\"{Binding StreamButtonText}\"")]
    [InlineData("AutomationProperties.Name=\"{Binding StreamButtonText}\"")]
    [InlineData("IsVisible=\"{Binding IsRecording}\"")]
    [InlineData("IsVisible=\"{Binding HasRecordingOutcome}\"")]
    [InlineData("Text=\"{Binding RecordingOutcome}\"")]
    [InlineData("Classes.saving=\"{Binding IsSavingRecording}\"")]
    [InlineData("IsVisible=\"{Binding ShowRecordingWarning}\"")]
    public void The_record_controls_are_bound(string binding) =>
        BindingFacts.AssertBinds("Daqifi.Avalonia/Views/MobileShellView.axaml", binding);

    /// <summary>
    /// The APP LOGS empty state used to promise logging the phone could not do. It must now name
    /// the control that records.
    /// </summary>
    [Fact]
    public void The_APP_LOGS_empty_state_names_the_record_switch()
    {
        var view = BindingFacts.Source("Daqifi.Avalonia/Views/Mobile/LoggedSessionsMobileView.axaml");

        Assert.DoesNotContain("Streamed sessions you log on this device appear here.", view, StringComparison.Ordinal);
        Assert.Contains("Record to APP LOGS", view, StringComparison.Ordinal);
    }

    private void StartStream()
    {
        _shell.StreamToggleCommand.Execute(null);
        Assert.True(_shell.IsStreaming, $"Arrangement: streaming did not start ({_shell.Status}).");
        Assert.True(
            _transport.WaitForSentText("SYSTem:StartStreamData", SendTimeout),
            "Arrangement: the device was never told to stream.");
        _transport.ClearSent();
    }

    /// <summary>
    /// The real APP LOGS view model over a stand-in for SQLite, with the ctor's first load finished.
    /// No dispatcher runs in this project, so <see cref="SerialTestUi"/> plays the UI thread: the
    /// view model is built on it, mutates its list on it, and the test reads the list on it.
    /// </summary>
    private async Task<LoggedSessionsMobileViewModel> OpenAppLogsAsync(CommittedSessions database)
    {
        var appLogs = await _ui.Get(() => new LoggedSessionsMobileViewModel(database.Read, _ui.Run));
        var deadline = DateTime.UtcNow + SendTimeout;
        while (true)
        {
            var (loaded, busy, failed, message) =
                await _ui.Get(() => (appLogs.Loaded, appLogs.IsBusy, appLogs.LoadFailed, appLogs.StatusMessage));
            if (loaded && !busy)
            {
                Assert.False(failed, message);
                return appLogs;
            }
            Assert.True(DateTime.UtcNow < deadline, "Arrangement: the first APP LOGS load never finished.");
            await Task.Delay(10);
        }
    }

    /// <summary>The listed session names, read on the UI thread.</summary>
    private Task<List<string>> ListedAsync(LoggedSessionsMobileViewModel appLogs) =>
        _ui.Get(() => appLogs.Sessions.Select(s => s.Name).ToList());

    /// <summary>A session whose one sample is held in memory, so the exporter needs no database.</summary>
    private static LoggingSession SessionWithOneSample(int id, string name)
    {
        var session = new LoggingSession(id, name);
        session.DataSamples.Add(new DataSample
        {
            LoggingSession = session,
            LoggingSessionID = id,
            DeviceName = "Nyquist",
            DeviceSerialNo = "SERIAL-1",
            ChannelName = "AI0",
            Color = "#FFD32F2F",
            Type = ChannelType.Analog,
            TimestampTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            Value = 1.5,
        });
        return session;
    }

    /// <summary>
    /// The refresh is fire-and-forget, so poll for its result rather than await it. Each poll is
    /// one UI-thread read, so it sees a load either before it starts or after it ends: the list,
    /// <c>IsBusy</c> and the status line are written on that thread, and <c>IsBusy</c> goes false
    /// last (and a queued refresh sets it again in the same step).
    /// </summary>
    private async Task WaitUntilListedAsync(LoggedSessionsMobileViewModel appLogs, string session)
    {
        var deadline = DateTime.UtcNow + SendTimeout;
        while (true)
        {
            var (names, busy) = await _ui.Get(() => (appLogs.Sessions.Select(s => s.Name).ToList(), appLogs.IsBusy));
            if (!busy && names.Contains(session)) { return; }
            Assert.True(
                DateTime.UtcNow < deadline,
                $"APP LOGS never listed {session} after the save was confirmed; it lists: {string.Join(", ", names)}.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Stands in for the session rows in SQLite: a read returns only what was committed when it ran.
    /// </summary>
    private sealed class CommittedSessions(params LoggingSession[] initial)
    {
        private readonly object _sync = new();
        private readonly List<LoggingSession> _committed = [.. initial];
        private readonly ManualResetEventSlim _readHeld = new();
        private SemaphoreSlim? _hold;
        private bool _failNext;

        public void Commit(LoggingSession session)
        {
            lock (_sync) { _committed.Add(session); }
        }

        /// <summary>The next read takes its snapshot, then waits for <paramref name="release"/>.</summary>
        public void HoldNextRead(SemaphoreSlim release)
        {
            lock (_sync) { _hold = release; }
        }

        public bool WaitForHeldRead(TimeSpan timeout) => _readHeld.Wait(timeout);

        /// <summary>The next read throws, as a failed SQLite query would.</summary>
        public void FailNextRead()
        {
            lock (_sync) { _failNext = true; }
        }

        public IReadOnlyList<LoggingSession> Read()
        {
            SemaphoreSlim? hold;
            List<LoggingSession> snapshot;
            lock (_sync)
            {
                if (_failNext)
                {
                    _failNext = false;
                    throw new InvalidOperationException("Test: the session query failed.");
                }
                snapshot = [.. _committed];
                hold = _hold;
                _hold = null;
            }
            if (hold is not null)
            {
                _readHeld.Set();
                hold.Wait(SendTimeout);
            }
            return snapshot;
        }
    }

    private sealed class FakeRecorder(CapturingTransport transport) : IMobileStreamRecorder
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public IReadOnlyList<IChannel> Channels { get; private set; } = [];
        public bool DeviceWasStreamingAtStart { get; private set; }
        public string? StartFailure { get; set; }
        private TaskCompletionSource<RecordingResult>? _pending;

        /// <summary>Plays the writer finishing: the last stopped recording's outcome becomes known.</summary>
        public void Complete(RecordingResult result) => Assert.True(_pending?.TrySetResult(result));

        public string? Start(IReadOnlyList<IChannel> channels)
        {
            Starts++;
            Channels = channels.ToList();
            DeviceWasStreamingAtStart =
                transport.SentText.Contains("SYSTem:StartStreamData", StringComparison.Ordinal);
            return StartFailure;
        }

        public Task<RecordingResult> StopAsync()
        {
            Stops++;
            _pending = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pending.Task;
        }
    }

    /// <summary>
    /// The file the APP LOGS save picker returns, backed by a local path. Avalonia 12 marks
    /// <see cref="IStorageFile"/> as not implementable by user code (an analyzer rule, enforced at
    /// compile time), and no public Avalonia type makes one from a path without a platform storage
    /// provider, so this double is generated at run time by <see cref="DispatchProxy"/>. It answers
    /// only the members the export writer uses; any other member throws, so a new dependency of the
    /// writer fails these tests instead of reading a silent default.
    /// </summary>
    public class LocalStorageFile : DispatchProxy
    {
        private string _path = string.Empty;

        public static IStorageFile Create(string path)
        {
            var file = Create<IStorageFile, LocalStorageFile>();
            ((LocalStorageFile)(object)file)._path = path;
            return file;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Name" => Path.GetFileName(_path),
            "OpenWriteAsync" => Task.FromResult<Stream>(new FileStream(_path, FileMode.Create, FileAccess.Write)),
            "GetBasicPropertiesAsync" => Task.FromResult(new StorageItemProperties(
                File.Exists(_path) ? (ulong)new FileInfo(_path).Length : null)),
            "DeleteAsync" => Task.Run(() => File.Delete(_path)),
            "Dispose" => null,
            _ => throw new NotSupportedException($"The export test double does not implement {targetMethod?.Name}."),
        };
    }
}
