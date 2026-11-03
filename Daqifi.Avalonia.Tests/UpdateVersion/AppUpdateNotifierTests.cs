using Daqifi.Desktop.UpdateVersion;
using Xunit;

namespace Daqifi.Avalonia.Tests.UpdateVersion;

/// <summary>
/// The app-update flow (#343): when an update is offered, and what installing it does in each case
/// the plan on #480 calls out. Runs against a fake service; no test touches the network.
/// </summary>
public class AppUpdateNotifierTests
{
    private sealed class FakeUpdateService : IAppUpdateService
    {
        public AppUpdateSupport Support { get; set; } = AppUpdateSupport.Supported;
        public bool ApplyNeedsElevation { get; set; }
        public AppUpdateOffer? Offer { get; set; } = new("3.4.0", "Notes");
        public Exception? CheckFailure { get; set; }
        public Exception? DownloadFailure { get; set; }
        public Action? OnDownload { get; set; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Applies { get; private set; }

        public Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            return CheckFailure is null ? Task.FromResult(Offer) : Task.FromException<AppUpdateOffer?>(CheckFailure);
        }

        public Task DownloadAsync(AppUpdateOffer offer, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            Downloads++;
            OnDownload?.Invoke();
            return DownloadFailure is null ? Task.CompletedTask : Task.FromException(DownloadFailure);
        }

        public void ApplyAndRestart(AppUpdateOffer offer) => Applies++;

        public bool InUseElsewhere { get; set; }
        public Exception? InUseCheckFailure { get; set; }

        public bool IsInstallInUseElsewhere() =>
            InUseCheckFailure is null ? InUseElsewhere : throw InUseCheckFailure;
    }

    private sealed class Harness
    {
        public FakeUpdateService Service { get; } = new();
        public UpdateBlocker Blocker { get; set; }
        public bool ConfirmAnswer { get; set; } = true;
        public List<string> Confirms { get; } = [];
        public List<string> Messages { get; } = [];
        public RecordingAppLogger Logger { get; } = new();

        /// <summary>Runs at each wait; the test uses it to finish what the update is waiting for.</summary>
        public Action<int>? OnDelay { get; set; }
        public int Delays { get; private set; }
        public List<int> AppliesAtEachDelay { get; } = [];

        private AppUpdateNotifier? _notifier;

        public AppUpdateNotifier Notifier => _notifier ??= new(
            Service,
            () => Blocker,
            (message, _) => { Confirms.Add(message); return Task.FromResult(ConfirmAnswer); },
            (message, _) => { Messages.Add(message); return Task.CompletedTask; },
            Logger,
            _ =>
            {
                Delays++;
                AppliesAtEachDelay.Add(Service.Applies);
                OnDelay?.Invoke(Delays);
                if (Delays > 1000) { throw new InvalidOperationException("waited forever"); }
                return Task.CompletedTask;
            });
    }

    [Theory]
    [InlineData(AppUpdateSupport.PlatformUnsupported)]
    [InlineData(AppUpdateSupport.NotInstalled)]
    [InlineData(AppUpdateSupport.OwnedExternally)]
    [InlineData(AppUpdateSupport.DisabledByPolicy)]
    public async Task UnsupportedCopies_NeverCheck(AppUpdateSupport support)
    {
        var h = new Harness();
        h.Service.Support = support;

        Assert.Null(await h.Notifier.CheckAsync());
        Assert.Equal(0, h.Service.Checks);
    }

    [Fact]
    public async Task CurrentCopy_GetsNoNotification()
    {
        var h = new Harness();
        h.Service.Offer = null;

        Assert.Null(await h.Notifier.CheckAsync());
    }

    [Fact]
    public async Task FailedCheck_IsAWarningNotAnError_AndShowsNothing()
    {
        var h = new Harness();
        h.Service.CheckFailure = new HttpRequestException("offline");

        Assert.Null(await h.Notifier.CheckAsync());
        Assert.Single(h.Logger.Warnings);
        Assert.Empty(h.Logger.Errors);
    }

    [Fact]
    public async Task AvailableUpdate_NamesTheVersion_AndDownloadsNothingUntilClicked()
    {
        var h = new Harness();

        var notification = await h.Notifier.CheckAsync();

        Assert.NotNull(notification);
        Assert.Contains("3.4.0", notification.Message);
        Assert.NotNull(notification.Action);
        Assert.Equal(0, h.Service.Downloads);
    }

    [Fact]
    public async Task ClickingInstall_DownloadsThenApplies()
    {
        var h = new Harness();
        var notification = await h.Notifier.CheckAsync();

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)notification!.Action!).ExecuteAsync(null);

        Assert.Equal(1, h.Service.Downloads);
        Assert.Equal(1, h.Service.Applies);
        Assert.Empty(h.Confirms);
    }

    [Fact]
    public async Task NothingRunning_InstallsAtOnce_WithoutAMessage()
    {
        var h = new Harness();

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(1, h.Service.Applies);
        Assert.Equal(0, h.Delays);
        Assert.Empty(h.Messages);
    }

    /// <summary>
    /// Installing restarts the app. Instead of refusing, the update downloads, says in one line what
    /// it waits for, and installs by itself when that is done: the user clicks once.
    /// </summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging, "when logging stops")]
    [InlineData(UpdateBlocker.FirmwareUpdate, "when the firmware update finishes")]
    [InlineData(UpdateBlocker.OtherWindowOpen, "when DAQiFi's other windows are closed")]
    [InlineData(UpdateBlocker.SdCardTransfer, "when the SD card transfer finishes")]
    [InlineData(UpdateBlocker.LoggedDataBusy, "when the import or delete finishes")]
    public async Task WhileBusy_DownloadsWaitsAndInstallsWhenDone(UpdateBlocker blocker, string waitingFor)
    {
        var h = new Harness { Blocker = blocker };
        h.OnDelay = n => { if (n == 3) { h.Blocker = UpdateBlocker.None; } };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(1, h.Service.Downloads);
        Assert.Contains(waitingFor, Assert.Single(h.Messages));
        Assert.Equal([0, 0, 0], h.AppliesAtEachDelay);
        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>A session, a flash or a dialog started while the update downloaded must not be cut off.</summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging)]
    [InlineData(UpdateBlocker.FirmwareUpdate)]
    [InlineData(UpdateBlocker.OtherWindowOpen)]
    [InlineData(UpdateBlocker.SdCardTransfer)]
    [InlineData(UpdateBlocker.LoggedDataBusy)]
    public async Task WorkStartedDuringDownload_IsWaitedFor(UpdateBlocker blocker)
    {
        var h = new Harness();
        h.Service.OnDownload = () => h.Blocker = blocker;
        h.OnDelay = n => { if (n == 2) { h.Blocker = UpdateBlocker.None; } };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal([0, 0], h.AppliesAtEachDelay);
        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>Messages stay one short line.</summary>
    [Fact]
    public async Task WaitingMessage_IsOneShortLine()
    {
        var h = new Harness { Blocker = UpdateBlocker.OtherWindowOpen };
        h.OnDelay = _ => h.Blocker = UpdateBlocker.None;

        await h.Notifier.InstallAsync(h.Service.Offer!);

        var message = Assert.Single(h.Messages);
        Assert.DoesNotContain('\n', message);
        Assert.True(message.Length <= 90, message);
    }

    /// <summary>
    /// Applying ends every process in the install folder, in every session. On a shared PC that is
    /// another person's recording (#503 review), so the update waits for other copies to close.
    /// </summary>
    [Fact]
    public async Task OtherCopyRunning_WaitsForItToClose()
    {
        var h = new Harness();
        h.Service.InUseElsewhere = true;
        h.OnDelay = n => { if (n == 2) { h.Service.InUseElsewhere = false; } };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Contains("other copies of DAQiFi", Assert.Single(h.Messages));
        Assert.Equal([0, 0], h.AppliesAtEachDelay);
        Assert.Equal(1, h.Service.Applies);
    }

    [Fact]
    public async Task OtherCopyStartedDuringDownload_IsWaitedFor()
    {
        var h = new Harness();
        h.Service.OnDownload = () => h.Service.InUseElsewhere = true;
        h.OnDelay = _ => h.Service.InUseElsewhere = false;

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal([0], h.AppliesAtEachDelay);
        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>When the check cannot tell, the update keeps waiting; the failure is logged once.</summary>
    [Fact]
    public async Task OtherCopyCheckFailing_KeepsWaiting_AndLogsOnce()
    {
        var h = new Harness();
        h.Service.InUseCheckFailure = new InvalidOperationException("no install folder");
        h.OnDelay = n => { if (n == 5) { h.Service.InUseCheckFailure = null; } };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal([0, 0, 0, 0, 0], h.AppliesAtEachDelay);
        Assert.Equal(1, h.Service.Applies);
        Assert.Single(h.Logger.Warnings);
    }

    /// <summary>This process's own reason is the one the user can act on here, so it is the one shown.</summary>
    [Fact]
    public async Task OwnBlocker_IsReportedBeforeAnotherCopy()
    {
        var h = new Harness { Blocker = UpdateBlocker.Logging };
        h.Service.InUseElsewhere = true;
        h.OnDelay = _ => { h.Blocker = UpdateBlocker.None; h.Service.InUseElsewhere = false; };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Contains("logging stops", Assert.Single(h.Messages));
    }

    /// <summary>A second click while an update waits must not download or install it twice.</summary>
    [Fact]
    public async Task SecondClickWhileWaiting_DoesNothing()
    {
        var h = new Harness { Blocker = UpdateBlocker.Logging };
        Task? second = null;
        h.OnDelay = n =>
        {
            if (n == 1) { second = h.Notifier.InstallAsync(h.Service.Offer!); }
            if (n == 2) { h.Blocker = UpdateBlocker.None; }
        };

        await h.Notifier.InstallAsync(h.Service.Offer!);
        await second!;

        Assert.Equal(1, h.Service.Downloads);
        Assert.Equal(1, h.Service.Applies);
        Assert.Single(h.Messages);
    }

    [Fact]
    public async Task AllUsersInstall_AsksFirst_AndDeclineDoesNothing()
    {
        var h = new Harness { ConfirmAnswer = false };
        h.Service.ApplyNeedsElevation = true;

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Contains("administrator", Assert.Single(h.Confirms));
        Assert.Equal(0, h.Service.Downloads);
        Assert.Equal(0, h.Service.Applies);
    }

    [Fact]
    public async Task AllUsersInstall_ConfirmedGoesAhead()
    {
        var h = new Harness();
        h.Service.ApplyNeedsElevation = true;

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Single(h.Confirms);
        Assert.Equal(1, h.Service.Applies);
    }

    [Fact]
    public async Task FailedDownload_IsReported_AndNothingIsApplied()
    {
        var h = new Harness();
        h.Service.DownloadFailure = new IOException("disk full");

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(0, h.Service.Applies);
        Assert.Contains("couldn't be downloaded", Assert.Single(h.Messages));
        Assert.Contains("Downloading", Assert.Single(h.Logger.Warnings));
    }

    [Theory]
    [InlineData(true, false, false, AppUpdateSupport.Supported)]
    [InlineData(false, false, false, AppUpdateSupport.NotInstalled)]
    [InlineData(true, false, true, AppUpdateSupport.DisabledByPolicy)]
    [InlineData(false, true, true, AppUpdateSupport.OwnedExternally)]
    [InlineData(false, false, true, AppUpdateSupport.NotInstalled)]
    public void ResolveSupport_ReportsTheMostSpecificReason(
        bool isInstalled, bool ownedExternally, bool disabledByPolicy, AppUpdateSupport expected) =>
        Assert.Equal(expected, AppUpdates.ResolveSupport(isInstalled, ownedExternally, disabledByPolicy));

    [Fact]
    public void HeadsThatRegisterNothing_ReportUnsupported() =>
        Assert.Equal(AppUpdateSupport.PlatformUnsupported, UnsupportedAppUpdateService.Instance.Support);
}
