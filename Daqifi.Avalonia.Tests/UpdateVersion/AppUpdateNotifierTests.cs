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

        /// <summary>
        /// Runs the notifier's watch for <paramref name="looks"/> looks, then stops it. <see cref="OnDelay"/>
        /// runs before each look, so a test changes what the update would cut off there.
        /// </summary>
        public async Task WatchAsync(int looks)
        {
            using var stop = new CancellationTokenSource();
            var last = Delays + looks;
            var onDelay = OnDelay;
            OnDelay = n =>
            {
                onDelay?.Invoke(n);
                if (n > last) { stop.Cancel(); }
            };
            try
            {
                await Notifier.WatchAsync(stop.Token);
            }
            finally
            {
                OnDelay = onDelay;
            }
        }

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
    /// installed, and the update is offered again (#503 review). Logging still runs, so the offer
    /// comes back with no button and says why, and gets its button back when logging stops (#348).
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
        Assert.Equal("DAQiFi 3.4.0 is available. Install it when logging stops.", h.Last.Message);
        Assert.Null(h.Last.ActionText);

        await h.WatchAsync(looks: 1);               // the next look sees logging stopped

        Assert.Equal("DAQiFi 3.4.0 is available.", h.Last.Message);
        Assert.Equal("Install and restart", h.Last.ActionText);
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>
    /// #348: while installing would cut work off, the offer has no button and says what it waits
    /// for. The ticket's three states first, then the others the app already waits for.
    /// </summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging, "when logging stops")]
    [InlineData(UpdateBlocker.FirmwareUpdate, "when the firmware update finishes")]
    [InlineData(UpdateBlocker.SdCardTransfer, "when the SD card transfer finishes")]
    [InlineData(UpdateBlocker.OtherWindowOpen, "when DAQiFi's other windows are closed")]
    [InlineData(UpdateBlocker.LoggedDataBusy, "when the import or delete finishes")]
    [InlineData(UpdateBlocker.ProfileBeingEdited, "when the profile editor is closed")]
    public async Task OfferWhileBusy_HasNoButton_AndSaysWhy(UpdateBlocker blocker, string waitingFor)
    {
        var h = new Harness { Blocker = blocker };

        var shown = await h.Notifier.CheckAsync();

        Assert.Same(shown, Assert.Single(h.Shown));
        Assert.Equal($"DAQiFi 3.4.0 is available. Install it {waitingFor}.", shown!.Message);
        Assert.Null(shown.ActionText);
        Assert.Null(shown.Action);
        Assert.Equal(0, h.Service.Downloads);
    }

    /// <summary>
    /// #348: when the work ends, the offer comes back by itself, with its button and with no new
    /// check, and the button then installs. Looks that see no change replace nothing, so the
    /// notification does not flicker. The watch itself never downloads or installs.
    /// </summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging)]
    [InlineData(UpdateBlocker.FirmwareUpdate)]
    [InlineData(UpdateBlocker.SdCardTransfer)]
    public async Task OfferWhileBusy_GetsItsButtonBack_WhenTheWorkEnds(UpdateBlocker blocker)
    {
        var h = new Harness { Blocker = blocker };
        await h.Notifier.CheckAsync();
        h.OnDelay = n => { if (n == 3) { h.Blocker = UpdateBlocker.None; } };

        await h.WatchAsync(looks: 5);

        Assert.Equal(2, h.Shown.Count);
        Assert.Equal("DAQiFi 3.4.0 is available.", h.Last.Message);
        Assert.Equal("Install and restart", h.Last.ActionText);
        Assert.Equal(1, h.Service.Checks);
        Assert.Equal(0, h.Service.Downloads);
        Assert.Equal(0, h.Service.Applies);

        await ((IAsyncRelayCommand)h.Last.Action!).ExecuteAsync(null);

        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>#348: work that starts while the offer is shown takes its button away until the work ends.</summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging, "when logging stops")]
    [InlineData(UpdateBlocker.FirmwareUpdate, "when the firmware update finishes")]
    [InlineData(UpdateBlocker.SdCardTransfer, "when the SD card transfer finishes")]
    public async Task WorkStartedWhileOffered_TakesTheButtonAway_UntilItEnds(UpdateBlocker blocker, string waitingFor)
    {
        var h = new Harness();
        await h.Notifier.CheckAsync();
        h.OnDelay = n =>
        {
            if (n == 2) { h.Blocker = blocker; }
            if (n == 4) { h.Blocker = UpdateBlocker.None; }
        };

        await h.WatchAsync(looks: 5);

        Assert.Equal(
            ["DAQiFi 3.4.0 is available.", $"DAQiFi 3.4.0 is available. Install it {waitingFor}.", "DAQiFi 3.4.0 is available."],
            h.Messages);
        Assert.Equal(["Install and restart", null, "Install and restart"], h.Shown.Select(n => n!.ActionText));
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>
    /// While an install step shows its own notification, the watch leaves it alone. Here logging
    /// starts during the download: the notification stays the download's, and the step then waits
    /// for logging to stop, as before #348.
    /// </summary>
    [Fact]
    public async Task Watch_LeavesAnInstallUnderWayAlone()
    {
        var h = new Harness();
        var downloaded = new TaskCompletionSource();
        h.Service.DownloadBody = _ => downloaded.Task;
        await h.Notifier.CheckAsync();
        var install = h.Notifier.InstallAsync(h.Service.Offer!);
        var shownBefore = h.Shown.Count;

        h.OnDelay = _ => h.Blocker = UpdateBlocker.Logging;
        await h.WatchAsync(looks: 3);

        Assert.Equal(shownBefore, h.Shown.Count);
        Assert.Equal("Downloading DAQiFi 3.4.0... 0%", h.Last.Message);

        h.OnDelay = _ => h.Blocker = UpdateBlocker.None;
        downloaded.SetResult();
        await install;

        Assert.Contains("Update ready. Installs when logging stops.", h.Messages);
        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>#348: "Try again" after a failure leads to an install too, so it follows the same rule.</summary>
    [Fact]
    public async Task FailedDownload_WhileWorkRuns_OffersNoRetryUntilItEnds()
    {
        var h = new Harness();
        h.Service.DownloadFailure = new IOException("disk full");
        h.Service.OnDownload = (_, _) => h.Blocker = UpdateBlocker.SdCardTransfer;

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal("Couldn't download the update. Try again when the SD card transfer finishes.", h.Last.Message);
        Assert.Null(h.Last.ActionText);

        h.OnDelay = _ => h.Blocker = UpdateBlocker.None;
        await h.WatchAsync(looks: 1);

        Assert.Equal("Couldn't download the update.", h.Last.Message);
        Assert.Equal("Try again", h.Last.ActionText);
    }

    /// <summary>
    /// #348: on an install for all users, "Update ready." waits for a click. Work that starts before
    /// the click takes the button away, and the button comes back when the work ends.
    /// </summary>
    [Fact]
    public async Task AllUsersInstall_ReadyLosesItsButton_WhileWorkRuns()
    {
        var h = new Harness { Blocker = UpdateBlocker.Logging };
        h.Service.ApplyNeedsElevation = true;
        h.OnDelay = n => { if (n == 1) { h.Blocker = UpdateBlocker.None; } };
        await h.Notifier.InstallAsync(h.Service.Offer!);
        Assert.Equal("Update ready.", h.Last.Message);

        h.OnDelay = _ => h.Blocker = UpdateBlocker.FirmwareUpdate;
        await h.WatchAsync(looks: 1);

        Assert.Equal("Update ready. Install it when the firmware update finishes.", h.Last.Message);
        Assert.Null(h.Last.ActionText);

        h.OnDelay = _ => h.Blocker = UpdateBlocker.None;
        await h.WatchAsync(looks: 1);

        Assert.Equal("Update ready.", h.Last.Message);
        Assert.Equal("Install and restart", h.Last.ActionText);
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>When the check cannot tell, the offer has no button until it can; the failure is logged once.</summary>
    [Fact]
    public async Task BlockerCheckThrowing_OfferHasNoButton_AndLogsOnce()
    {
        var h = new Harness();
        var throwing = true;
        h.BlockerSource = () => throwing ? throw new InvalidOperationException("view gone") : UpdateBlocker.None;

        var shown = await h.Notifier.CheckAsync();

        Assert.Equal("DAQiFi 3.4.0 is available. Install it when DAQiFi is idle.", shown!.Message);
        Assert.Null(shown.ActionText);

        h.OnDelay = n => { if (n == 3) { throwing = false; } };
        await h.WatchAsync(looks: 3);

        Assert.Equal("Install and restart", h.Last.ActionText);
        Assert.Single(h.Logger.Warnings);
    }

    /// <summary>The offer's text while it waits stays one short line, for every reason it can give.</summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging)]
    [InlineData(UpdateBlocker.FirmwareUpdate)]
    [InlineData(UpdateBlocker.SdCardTransfer)]
    [InlineData(UpdateBlocker.OtherWindowOpen)]
    [InlineData(UpdateBlocker.LoggedDataBusy)]
    [InlineData(UpdateBlocker.ProfileBeingEdited)]
    public async Task OfferWhileBusy_IsOneShortLine(UpdateBlocker blocker)
    {
        // The longest offer: the retry after an install for all users did not happen.
        var marker = TempMarker();
        marker.Remember("3.4.0");
        var h = new Harness { Blocker = blocker, Attempts = marker };
        h.Service.ApplyNeedsElevation = true;

        var shown = await h.Notifier.CheckAsync();

        Assert.StartsWith("DAQiFi 3.4.0 wasn't installed. Try again when ", shown!.Message);
        Assert.Null(shown.ActionText);
        Assert.DoesNotContain('\n', shown.Message);
        Assert.True(shown.Message.Length <= 90, shown.Message);
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

    /// <summary>
    /// The confirmation before the download says when the administrator prompt comes: the updater
    /// shows it after DAQiFi closes, not at once (#503 review).
    /// </summary>
    [Fact]
    public async Task AllUsersInstall_SaysThePromptComesWhenDaqifiRestarts()
    {
        var h = new Harness();
        h.Service.ApplyNeedsElevation = true;

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(
            "Installing needs administrator permission. Windows will ask when DAQiFi restarts.",
            Assert.Single(h.Confirms));
    }

    /// <summary>
    /// On an install for all users, an update that had to wait does not install by itself. The wait
    /// can end hours later, when no one is at the PC or only a standard user is; the restart would
    /// close DAQiFi and leave an administrator prompt that no one answers. So the wait ends with
    /// "Update ready." and an "Install and restart" button, and only that click applies (#503 review).
    /// </summary>
    [Fact]
    public async Task AllUsersInstall_AfterAWait_AppliesOnlyOnASecondClick()
    {
        var h = new Harness { Blocker = UpdateBlocker.SdCardTransfer };
        h.Service.ApplyNeedsElevation = true;
        h.OnDelay = n => { if (n == 2) { h.Blocker = UpdateBlocker.None; } };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(0, h.Service.Applies);
        Assert.Equal("Update ready.", h.Last.Message);
        Assert.Equal("Install and restart", h.Last.ActionText);
        Assert.Single(h.Confirms);

        await ((IAsyncRelayCommand)h.Last.Action!).ExecuteAsync(null);

        Assert.Equal(1, h.Service.Applies);
        Assert.Equal(1, h.Service.Downloads);   // the click installs what was downloaded
        Assert.Single(h.Confirms);              // the click is the answer; Windows asks next
    }

    /// <summary>While it waits, an update for all users says that it will ask, not that it will install.</summary>
    [Fact]
    public async Task AllUsersInstall_WhileWaiting_SaysItWillAsk()
    {
        var h = new Harness { Blocker = UpdateBlocker.Logging };
        h.Service.ApplyNeedsElevation = true;
        h.OnDelay = n => { if (n == 1) { h.Blocker = UpdateBlocker.None; } };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Contains("Update ready. Asks to install when logging stops.", h.Messages);
        Assert.DoesNotContain(h.Messages, m => m.Contains("Installs when"));
    }

    /// <summary>
    /// Work that starts between "Update ready." and the click is waited for again, and the wait
    /// again ends with a question, not with an install.
    /// </summary>
    [Fact]
    public async Task AllUsersInstall_WorkStartedBeforeTheSecondClick_IsWaitedFor_ThenAsksAgain()
    {
        var h = new Harness { Blocker = UpdateBlocker.Logging };
        h.Service.ApplyNeedsElevation = true;
        h.OnDelay = n => { if (n == 1 || n == 2) { h.Blocker = UpdateBlocker.None; } };

        await h.Notifier.InstallAsync(h.Service.Offer!);
        Assert.Equal("Update ready.", h.Last.Message);

        h.Blocker = UpdateBlocker.FirmwareUpdate;   // a flash starts before the user clicks
        await ((IAsyncRelayCommand)h.Last.Action!).ExecuteAsync(null);

        Assert.Equal(0, h.Service.Applies);
        Assert.Contains("Update ready. Asks to install when the firmware update finishes.", h.Messages);
        Assert.Equal("Update ready.", h.Last.Message);

        await ((IAsyncRelayCommand)h.Last.Action!).ExecuteAsync(null);
        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>An install for one user needs no prompt, so it still installs by itself after a wait.</summary>
    [Fact]
    public async Task OneUserInstall_AfterAWait_StillInstallsByItself()
    {
        var h = new Harness { Blocker = UpdateBlocker.Logging };
        h.OnDelay = n => { if (n == 2) { h.Blocker = UpdateBlocker.None; } };

        await h.Notifier.InstallAsync(h.Service.Offer!);

        Assert.Equal(1, h.Service.Applies);
        Assert.Empty(h.Confirms);
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
