using CommunityToolkit.Mvvm.Input;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.UpdateVersion;
using Xunit;

namespace Daqifi.Avalonia.Tests.UpdateVersion;

/// <summary>
/// The app-update flow (#343): when an update is offered, and what installing it does in each case
/// the plan on #480 calls out. Runs against a fake service; no test touches the network. The
/// update's state lives in one notification, replaced at each step, so these tests read the
/// sequence of notifications shown.
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
        public Action<IProgress<int>?, CancellationToken>? OnDownload { get; set; }
        public Func<CancellationToken, Task>? DownloadBody { get; set; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Applies { get; private set; }
        public Exception? ApplyFailure { get; set; }
        public bool InUseElsewhere { get; set; }
        public Exception? InUseCheckFailure { get; set; }

        public Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            return CheckFailure is null ? Task.FromResult(Offer) : Task.FromException<AppUpdateOffer?>(CheckFailure);
        }

        public async Task DownloadAsync(AppUpdateOffer offer, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            Downloads++;
            OnDownload?.Invoke(progress, cancellationToken);
            if (DownloadBody is not null) { await DownloadBody(cancellationToken); }
            if (DownloadFailure is not null) { throw DownloadFailure; }
        }

        public void ApplyAndRestart(AppUpdateOffer offer)
        {
            Applies++;
            if (ApplyFailure is not null) { throw ApplyFailure; }
        }

        public bool IsInstallInUseElsewhere() =>
            InUseCheckFailure is null ? InUseElsewhere : throw InUseCheckFailure;
    }

    private sealed class Harness
    {
        public FakeUpdateService Service { get; } = new();
        public Func<UpdateBlocker> BlockerSource { get; set; }
        public UpdateBlocker Blocker { get; set; }
        public bool ConfirmAnswer { get; set; } = true;
        public List<string> Confirms { get; } = [];
        public List<Notifications?> Shown { get; } = [];
        public RecordingAppLogger Logger { get; } = new();

        /// <summary>Runs at each wait; the test uses it to finish what the update is waiting for.</summary>
        public Action<int>? OnDelay { get; set; }
        public int Delays { get; private set; }
        public List<int> AppliesAtEachDelay { get; } = [];

        private AppUpdateNotifier? _notifier;

        public UpdateAttemptMarker? Attempts { get; init; }

        public Harness() => BlockerSource = () => Blocker;

        public AppUpdateNotifier Notifier => _notifier ??= new(
            Service,
            () => BlockerSource(),
            (message, _) => { Confirms.Add(message); return Task.FromResult(ConfirmAnswer); },
            notice => Shown.Add(notice),
            Logger,
            (_, token) =>
            {
                Delays++;
                AppliesAtEachDelay.Add(Service.Applies);
                OnDelay?.Invoke(Delays);
                if (Delays > 1000) { throw new InvalidOperationException("waited forever"); }
                return token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;
            },
            Attempts);

        public Notifications Last => Shown.Last() ?? throw new InvalidOperationException("removed");
        public List<string> Messages => Shown.Select(n => n?.Message ?? "").ToList();

        /// <summary>Clicks the button on the notification currently shown.</summary>
        public void ClickCurrent()
        {
            switch (Last.Action)
            {
                case IAsyncRelayCommand asyncCommand: _ = asyncCommand.ExecuteAsync(null); break;
                case { } command: command.Execute(null); break;
            }
        }
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
        Assert.Empty(h.Shown);
    }

    [Fact]
    public async Task CurrentCopy_GetsNoNotification()
    {
        var h = new Harness();
        h.Service.Offer = null;

        Assert.Null(await h.Notifier.CheckAsync());
        Assert.Empty(h.Shown);
    }

    /// <summary>A request that times out is cancelled by the HTTP stack, not by the caller: still "offline", never thrown.</summary>
    [Fact]
    public async Task TimedOutCheck_IsAWarning_AndDoesNotThrow()
    {
        var h = new Harness();
        h.Service.CheckFailure = new TaskCanceledException("timed out");

        Assert.Null(await h.Notifier.CheckAsync());
        Assert.Single(h.Logger.Warnings);
        Assert.Empty(h.Shown);
    }

    [Fact]
    public async Task CallerCancellingTheCheck_Propagates()
    {
        var h = new Harness();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        h.Service.CheckFailure = new OperationCanceledException(cancel.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Notifier.CheckAsync(cancel.Token));
        Assert.Empty(h.Logger.Warnings);
    }

    [Fact]
    public async Task FailedCheck_IsAWarningNotAnError_AndShowsNothing()
    {
        var h = new Harness();
        h.Service.CheckFailure = new HttpRequestException("offline");

        Assert.Null(await h.Notifier.CheckAsync());
        Assert.Single(h.Logger.Warnings);
        Assert.Empty(h.Logger.Errors);
        Assert.Empty(h.Shown);
    }

    [Fact]
    public async Task AvailableUpdate_IsShown_NamesTheVersion_AndDownloadsNothingUntilClicked()
    {
        var h = new Harness();

        var notification = await h.Notifier.CheckAsync();

        Assert.Same(notification, Assert.Single(h.Shown));
        Assert.Contains("3.4.0", notification!.Message);
        Assert.Equal("Install and restart", notification.ActionText);
        Assert.Equal(0, h.Service.Downloads);
    }

    [Fact]
    public async Task ClickingInstall_DownloadsThenApplies()
    {
        var h = new Harness();
        var notification = await h.Notifier.CheckAsync();

        await ((IAsyncRelayCommand)notification!.Action!).ExecuteAsync(null);

        Assert.Equal(1, h.Service.Downloads);
        Assert.Equal(1, h.Service.Applies);
        Assert.Empty(h.Confirms);
    }

    /// <summary>
    /// The user sees their click did something: the notification becomes the download's percentage,
    /// once per whole percent (#503 review).
    /// </summary>
    [Fact]
    public async Task Download_ShowsItsPercentage_InTheNotification()
    {
        var h = new Harness();
        h.Service.OnDownload = (progress, _) =>
        {
            progress!.Report(0);
            progress.Report(40);
            progress.Report(40);
            progress.Report(100);
        };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(
            ["Downloading DAQiFi 3.4.0... 0%", "Downloading DAQiFi 3.4.0... 40%", "Downloading DAQiFi 3.4.0... 100%"],
            h.Messages);
        Assert.All(h.Shown, n => Assert.Equal("Cancel", n!.ActionText));
    }

    /// <summary>Cancel during the download stops it, installs nothing, and offers the update again.</summary>
    [Fact]
    public async Task CancelDuringDownload_StopsIt_AndOffersTheUpdateAgain()
    {
        var h = new Harness();
        h.Service.DownloadBody = async token =>
        {
            h.ClickCurrent();                       // Cancel on "Downloading..."
            // Bounded, so a Cancel that does nothing fails this test instead of hanging it.
            await Task.Delay(TimeSpan.FromSeconds(10), token);
        };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(0, h.Service.Applies);
        Assert.Equal("Install and restart", h.Last.ActionText);
        Assert.Contains("3.4.0 is available", h.Last.Message);
        Assert.Empty(h.Logger.Warnings);
    }

    [Fact]
    public async Task NothingRunning_InstallsAtOnce_WithoutWaiting()
    {
        var h = new Harness();

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(1, h.Service.Applies);
        Assert.Equal(0, h.Delays);
        Assert.DoesNotContain(h.Messages, m => m.StartsWith("Update ready"));
    }

    /// <summary>
    /// Installing restarts the app. Instead of refusing, the update downloads, says in the
    /// notification what it waits for, and installs by itself when that is done.
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
        Assert.Contains($"Update ready. Installs {waitingFor}.", h.Messages);
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
        h.Service.OnDownload = (_, _) => h.Blocker = blocker;
        h.OnDelay = n => { if (n == 2) { h.Blocker = UpdateBlocker.None; } };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal([0, 0], h.AppliesAtEachDelay);
        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>
    /// The user decides when the app restarts: Cancel on a waiting update stops the wait, nothing is
    /// installed, and the update is offered again (#503 review).
    /// </summary>
    [Fact]
    public async Task CancelWhileWaiting_StopsTheWait_AndOffersTheUpdateAgain()
    {
        var h = new Harness { Blocker = UpdateBlocker.Logging };
        h.OnDelay = n =>
        {
            if (n == 2) { h.ClickCurrent(); }       // Cancel on "Update ready..."
            if (n == 3) { h.Blocker = UpdateBlocker.None; }
        };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(0, h.Service.Applies);
        Assert.Equal(2, h.Delays);
        Assert.Equal("Install and restart", h.Last.ActionText);
    }

    /// <summary>Messages stay one short line.</summary>
    [Fact]
    public async Task EveryMessage_IsOneShortLine()
    {
        var h = new Harness { Blocker = UpdateBlocker.OtherWindowOpen };
        h.Service.InUseElsewhere = true;
        h.OnDelay = n =>
        {
            if (n == 1) { h.Blocker = UpdateBlocker.None; }
            if (n == 2) { h.Service.InUseElsewhere = false; }
        };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.All(h.Messages, message =>
        {
            Assert.DoesNotContain('\n', message);
            Assert.True(message.Length <= 90, message);
        });
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

        Assert.Contains(h.Messages, m => m.Contains("other copies of DAQiFi"));
        Assert.Equal([0, 0], h.AppliesAtEachDelay);
        Assert.Equal(1, h.Service.Applies);
    }

    [Fact]
    public async Task OtherCopyStartedDuringDownload_IsWaitedFor()
    {
        var h = new Harness();
        h.Service.OnDownload = (_, _) => h.Service.InUseElsewhere = true;
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

    /// <summary>
    /// A throwing blocker check must not fault the install silently (#503 review): it is treated
    /// like the other-copies check, waited on and logged once.
    /// </summary>
    [Fact]
    public async Task BlockerCheckThrowing_KeepsWaiting_AndLogsOnce()
    {
        var h = new Harness();
        var throwing = true;
        h.BlockerSource = () => throwing ? throw new InvalidOperationException("view gone") : UpdateBlocker.None;
        h.OnDelay = n => { if (n == 3) { throwing = false; } };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal([0, 0, 0], h.AppliesAtEachDelay);
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

        Assert.Contains(h.Messages, m => m.Contains("logging stops"));
        Assert.DoesNotContain(h.Messages, m => m.Contains("other copies"));
    }

    /// <summary>A second click while an update waits must not download or install it twice.</summary>
    [Fact]
    public async Task SecondInstallWhileWaiting_DoesNothing()
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
        Assert.Empty(h.Shown);
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
    public async Task FailedDownload_SaysSoInOneLine_AndOffersARetry()
    {
        var h = new Harness();
        h.Service.DownloadFailure = new IOException("disk full");

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(0, h.Service.Applies);
        Assert.Equal("Couldn't download the update.", h.Last.Message);
        Assert.Equal("Try again", h.Last.ActionText);
        Assert.Contains("Downloading", Assert.Single(h.Logger.Warnings));
    }

    [Fact]
    public async Task FailedApply_SaysSoInOneLine_AndOffersARetry()
    {
        var h = new Harness();
        h.Service.ApplyFailure = new InvalidOperationException("locked");

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal("Couldn't install the update.", h.Last.Message);
        Assert.Equal("Try again", h.Last.ActionText);
        Assert.Single(h.Logger.Errors);
    }

    private static UpdateAttemptMarker TempMarker() =>
        new(Path.Combine(Path.GetTempPath(), $"daqifi-update-attempt-{Guid.NewGuid():N}", "update-attempt"));

    /// <summary>
    /// Installing restarts the app through the updater. When the administrator prompt is cancelled
    /// the old version comes back as a new process, which used to offer the same update with no
    /// word of why. It now says the update wasn't installed and what it needs.
    /// </summary>
    [Fact]
    public async Task CancelledAllUsersInstall_SaysWhyOnTheNextStart()
    {
        var marker = TempMarker();
        var before = new Harness { Attempts = marker };
        before.Service.ApplyNeedsElevation = true;
        await before.Notifier.InstallAsync(before.Service.Offer!);
        Assert.Equal(1, before.Service.Applies);

        // The copy that starts again: same version, so the same update is on offer.
        var after = new Harness { Attempts = marker };
        after.Service.ApplyNeedsElevation = true;
        var shown = await after.Notifier.CheckAsync();

        Assert.Equal("DAQiFi 3.4.0 wasn't installed. An administrator must approve the update.", shown!.Message);
        Assert.Equal("Try again", shown.ActionText);

        // Said once: the next start is back to the plain offer.
        var later = new Harness { Attempts = marker };
        Assert.Equal("DAQiFi 3.4.0 is available.", (await later.Notifier.CheckAsync())!.Message);
    }

    [Fact]
    public async Task InstallThatWorked_ForgetsTheAttempt_Silently()
    {
        var marker = TempMarker();
        marker.Remember("3.4.0");

        var current = new Harness { Attempts = marker };
        current.Service.Offer = null;
        Assert.Null(await current.Notifier.CheckAsync());
        Assert.Null(marker.Take());

        marker.Remember("3.4.0");
        var newer = new Harness { Attempts = marker };
        newer.Service.Offer = new("3.5.0", "Notes");
        Assert.Equal("DAQiFi 3.5.0 is available.", (await newer.Notifier.CheckAsync())!.Message);
    }

    /// <summary>Offline, the check says nothing, so the attempt is kept for one that can.</summary>
    [Fact]
    public async Task FailedCheck_KeepsTheAttempt()
    {
        var marker = TempMarker();
        marker.Remember("3.4.0");
        var offline = new Harness { Attempts = marker };
        offline.Service.CheckFailure = new HttpRequestException("offline");

        Assert.Null(await offline.Notifier.CheckAsync());
        Assert.Equal("3.4.0", marker.Take());
    }

    [Fact]
    public void Marker_ThatCannotBeWritten_NeverThrows()
    {
        var marker = new UpdateAttemptMarker(Path.Combine(Path.GetTempPath(), "\0bad", "update-attempt"));
        marker.Remember("3.4.0");
        Assert.Null(marker.Take());
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
