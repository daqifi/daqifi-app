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

        public bool IsInstallInUseElsewhere() => false;
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

        private AppUpdateViewModel? _viewModel;

        public AppUpdateViewModel ViewModel => _viewModel ??= new(
            Service,
            () => Blocker,
            (_, _) => Task.FromResult(true),
            notice => Bell.Add(notice),
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
    /// One source of truth: the drawer shows the very notification the bell shows. Nothing is
    /// downloaded until the button is clicked (#341), and "Check now" gives its place to that button.
    /// </summary>
    [Fact]
    public async Task FoundUpdate_DrawerAndBellShowTheSameNotification()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);

        await h.CheckNowAsync();

        Assert.Same(Assert.Single(h.Bell), h.ViewModel.Notice);
        Assert.Equal("DAQiFi 3.4.0 is available.", h.ViewModel.Status);
        Assert.Equal("Install and restart", h.ViewModel.ActionText);
        Assert.True(h.ViewModel.HasAction);
        Assert.False(h.ViewModel.CanCheckNow);
        Assert.Equal(0, h.Service.Downloads);
    }

    [Fact]
    public async Task InstallFromTheDrawer_ShowsTheDownload_ThenRestarts()
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

        var install = h.ClickAction();

        Assert.Equal("Downloading DAQiFi 3.4.0... 42%", h.ViewModel.Status);
        Assert.Equal("Cancel", h.ViewModel.ActionText);
        Assert.Same(h.Bell.Last(), h.ViewModel.Notice);
        Assert.Equal(0, h.Service.Applies);

        downloading.SetResult();
        await install;

        Assert.Equal(1, h.Service.Applies);
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
        Assert.Equal("Install and restart", h.ViewModel.ActionText);
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>The section follows #348 as the bell does: no install button while a restart would stop work.</summary>
    [Fact]
    public async Task WhileLogging_TheOfferSaysWhatItWaitsFor_AndHasNoButton()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        h.Blocker = UpdateBlocker.Logging;

        await h.CheckNowAsync();

        Assert.Equal("DAQiFi 3.4.0 is available. Install it when logging stops.", h.ViewModel.Status);
        Assert.False(h.ViewModel.HasAction);
        Assert.False(h.ViewModel.CanCheckNow);
    }

    /// <summary>
    /// The issue's "ready to restart" state. On an install for all users, a downloaded update that
    /// had to wait does not restart by itself; it shows "Update ready." and a button (#503).
    /// </summary>
    [Fact]
    public async Task AllUsersInstall_AfterAWait_ShowsUpdateReady_WithAButton()
    {
        using var h = new Harness();
        h.Service.Offer = new("3.4.0", null);
        h.Service.ApplyNeedsElevation = true;
        h.Service.DownloadBody = (_, _) =>
        {
            h.Blocker = UpdateBlocker.Logging;
            return Task.CompletedTask;
        };
        await h.CheckNowAsync();

        var install = h.ClickAction();
        await h.Waits.Pending(AppUpdateNotifier.RecheckInterval);

        Assert.Equal("Update ready. Asks to install when logging stops.", h.ViewModel.Status);

        h.Blocker = UpdateBlocker.None;
        h.Waits.Release(AppUpdateNotifier.RecheckInterval);
        await install;

        Assert.Equal("Update ready.", h.ViewModel.Status);
        Assert.Equal("Install and restart", h.ViewModel.ActionText);
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>This app stays open for hours: it checks again on a timer, and stops once an update is offered.</summary>
    [Fact]
    public async Task NoUpdate_ChecksAgainAfterTheInterval_AndStopsOnceOneIsOffered()
    {
        using var h = new Harness();
        var run = h.ViewModel.RunAsync();
        h.Waits.Release(AppUpdateViewModel.StartupDelay);
        await h.Waits.Pending(AppUpdateViewModel.CheckInterval);
        Assert.Equal(1, h.Service.Checks);

        h.Service.Offer = new("3.4.0", null);
        h.Waits.Release(AppUpdateViewModel.CheckInterval);
        await h.Waits.Pending(AppUpdateViewModel.CheckInterval);

        Assert.Equal(2, h.Service.Checks);
        Assert.Equal("DAQiFi 3.4.0 is available.", h.ViewModel.Status);

        h.Waits.Release(AppUpdateViewModel.CheckInterval);
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, h.Service.Checks);
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
}
