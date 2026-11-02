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
    }

    private sealed class Harness
    {
        public FakeUpdateService Service { get; } = new();
        public UpdateBlocker Blocker { get; set; }
        public bool ConfirmAnswer { get; set; } = true;
        public List<string> Confirms { get; } = [];
        public List<string> Messages { get; } = [];
        public RecordingAppLogger Logger { get; } = new();

        public AppUpdateNotifier Notifier => new(
            Service,
            () => Blocker,
            (message, _) => { Confirms.Add(message); return Task.FromResult(ConfirmAnswer); },
            (message, _) => { Messages.Add(message); return Task.CompletedTask; },
            Logger);
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
    public async Task WhileLogging_InstallRefusesAndSaysWhy()
    {
        var h = new Harness { Blocker = UpdateBlocker.Logging };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(0, h.Service.Downloads);
        Assert.Equal(0, h.Service.Applies);
        Assert.Contains("Stop logging", Assert.Single(h.Messages));
    }

    /// <summary>Restarting mid-flash can leave the device unusable, so a flash blocks the install too.</summary>
    [Fact]
    public async Task WhileFirmwareUpdating_InstallRefusesAndSaysWhy()
    {
        var h = new Harness { Blocker = UpdateBlocker.FirmwareUpdate };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(0, h.Service.Downloads);
        Assert.Equal(0, h.Service.Applies);
        Assert.Contains("firmware update", Assert.Single(h.Messages));
    }

    [Fact]
    public async Task FirmwareUpdateStartedDuringDownload_DoesNotRestart()
    {
        var h = new Harness();
        h.Service.OnDownload = () => h.Blocker = UpdateBlocker.FirmwareUpdate;

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(1, h.Service.Downloads);
        Assert.Equal(0, h.Service.Applies);
        Assert.Contains("firmware update", Assert.Single(h.Messages));
    }

    [Fact]
    public async Task LoggingStartedDuringDownload_DoesNotRestart()
    {
        var h = new Harness();
        h.Service.OnDownload = () => h.Blocker = UpdateBlocker.Logging;

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(1, h.Service.Downloads);
        Assert.Equal(0, h.Service.Applies);
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
        Assert.Contains("disk full", Assert.Single(h.Messages));
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
