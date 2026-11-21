using CommunityToolkit.Mvvm.Input;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.UpdateVersion;
using Xunit;

namespace Daqifi.Avalonia.Tests.UpdateVersion;

/// <summary>
/// The app-update flow (#343): when an update is offered, what downloading it does, and what
/// "Restart to update" does in each case the plan on #480 calls out. Since the #346 review the
/// download and the restart are two clicks: a finished download never restarts the app by itself.
/// Runs against a fake service; no test touches the network. The update's state lives in one
/// notification, replaced at each step, so these tests read the sequence of notifications shown.
/// </summary>
public class AppUpdateNotifierTests
{
    private const string READY = "DAQiFi 3.4.0 is ready.";
    private const string RESTART = "Restart to update";
    private const string DOWNLOAD = "Download update";

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

        /// <summary>Runs at each look of the watch; the test uses it to start or end work there.</summary>
        public Action<int>? OnDelay { get; set; }
        public int Delays { get; private set; }

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
                OnDelay?.Invoke(Delays);
                if (Delays > 1000) { throw new InvalidOperationException("waited forever"); }
                return token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;
            },
            Attempts);

        public Notifications Last => Shown.Last() ?? throw new InvalidOperationException("removed");
        public List<string> Messages => Shown.Select(n => n?.Message ?? "").ToList();

        /// <summary>
        /// Runs the notifier's watch for <paramref name="looks"/> looks, then stops it. <see cref="OnDelay"/>
        /// runs before each look, so a test changes what the restart would cut off there.
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

        /// <summary>Clicks the button on the notification currently shown, without waiting for what it starts.</summary>
        public void ClickCurrent()
        {
            switch (Last.Action)
            {
                case IAsyncRelayCommand asyncCommand: _ = asyncCommand.ExecuteAsync(null); break;
                case { } command: command.Execute(null); break;
            }
        }

        /// <summary>Clicks the button on the notification currently shown and waits for what it starts.</summary>
        public Task ClickAsync() => ((IAsyncRelayCommand)Last.Action!).ExecuteAsync(null);

        /// <summary>Downloads the offer, which ends with "ready" and its "Restart to update" button.</summary>
        public Task DownloadAsync() => Notifier.InstallAsync(Service.Offer!);
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
        Assert.True(h.Notifier.LastCheckFailed);
    }

    [Fact]
    public async Task AvailableUpdate_IsShown_NamesTheVersion_AndDownloadsNothingUntilClicked()
    {
        var h = new Harness();

        var notification = await h.Notifier.CheckAsync();

        Assert.Same(notification, Assert.Single(h.Shown));
        Assert.Contains("3.4.0", notification!.Message);
        Assert.Equal(DOWNLOAD, notification.ActionText);
        Assert.Equal(0, h.Service.Downloads);
        Assert.True(h.Notifier.HasOffer);
        Assert.False(h.Notifier.LastCheckFailed);
    }

    // ---- The download, then a separate restart (#346 review) ----

    [Fact]
    public async Task ClickingDownload_Downloads_ThenOffersTheRestart()
    {
        var h = new Harness();
        await h.Notifier.CheckAsync();

        await h.ClickAsync();

        Assert.Equal(1, h.Service.Downloads);
        Assert.Equal(0, h.Service.Applies);
        Assert.Equal(READY, h.Last.Message);
        Assert.Equal(RESTART, h.Last.ActionText);
        Assert.Empty(h.Confirms);

        await h.ClickAsync();

        Assert.Equal(1, h.Service.Applies);
        Assert.Equal(1, h.Service.Downloads);   // the click installs what was downloaded
    }

    /// <summary>
    /// The user sees their click did something: the notification becomes the download's percentage,
    /// once per whole percent (#503 review), and then says the update is ready.
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

        await h.DownloadAsync();

        Assert.Equal(
            ["Downloading DAQiFi 3.4.0... 0%", "Downloading DAQiFi 3.4.0... 40%", "Downloading DAQiFi 3.4.0... 100%", READY],
            h.Messages);
        Assert.All(h.Shown.Take(3), n => Assert.Equal("Cancel", n!.ActionText));
        Assert.Equal(RESTART, h.Last.ActionText);
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

        await h.DownloadAsync();

        Assert.Equal(0, h.Service.Applies);
        Assert.Equal(DOWNLOAD, h.Last.ActionText);
        Assert.Contains("3.4.0 is available", h.Last.Message);
        Assert.Empty(h.Logger.Warnings);
    }

    /// <summary>While a download runs, its own notification is shown and a new check would replace it.</summary>
    [Fact]
    public async Task DuringADownload_ThereIsNoOffer()
    {
        var h = new Harness();
        var downloaded = new TaskCompletionSource();
        h.Service.DownloadBody = _ => downloaded.Task;
        await h.Notifier.CheckAsync();
        Assert.True(h.Notifier.HasOffer);

        var download = h.DownloadAsync();
        Assert.False(h.Notifier.HasOffer);

        downloaded.SetResult();
        await download;
        Assert.True(h.Notifier.HasOffer);
    }

    [Fact]
    public async Task NothingRunning_RestartInstallsAtOnce()
    {
        var h = new Harness();
        await h.DownloadAsync();

        await h.ClickAsync();

        Assert.Equal(1, h.Service.Applies);
        Assert.Equal(0, h.Delays);
    }

    /// <summary>
    /// Downloading stops nothing, so the offer has its button while work runs. Only the restart
    /// waits for the work (#348, #653).
    /// </summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging)]
    [InlineData(UpdateBlocker.FirmwareUpdate)]
    [InlineData(UpdateBlocker.SdCardTransfer)]
    [InlineData(UpdateBlocker.OtherWindowOpen)]
    [InlineData(UpdateBlocker.LoggedDataBusy)]
    [InlineData(UpdateBlocker.ProfileBeingEdited)]
    public async Task OfferWhileBusy_StillDownloads(UpdateBlocker blocker)
    {
        var h = new Harness { Blocker = blocker };

        var shown = await h.Notifier.CheckAsync();

        Assert.Equal("DAQiFi 3.4.0 is available.", shown!.Message);
        Assert.Equal(DOWNLOAD, shown.ActionText);

        await h.ClickAsync();

        Assert.Equal(1, h.Service.Downloads);
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>
    /// #348: while the restart would cut work off, "ready" has no button and says what it waits
    /// for. The ticket's three states first, then the others the app already waits for.
    /// </summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging, "when logging stops")]
    [InlineData(UpdateBlocker.FirmwareUpdate, "when the firmware update finishes")]
    [InlineData(UpdateBlocker.SdCardTransfer, "when the SD card transfer finishes")]
    [InlineData(UpdateBlocker.OtherWindowOpen, "when DAQiFi's other windows are closed")]
    [InlineData(UpdateBlocker.LoggedDataBusy, "when the import or delete finishes")]
    [InlineData(UpdateBlocker.ProfileBeingEdited, "when the profile editor is closed")]
    public async Task ReadyWhileBusy_HasNoButton_AndSaysWhy(UpdateBlocker blocker, string waitingFor)
    {
        var h = new Harness { Blocker = blocker };

        await h.DownloadAsync();

        Assert.Equal($"DAQiFi 3.4.0 is ready. Restart {waitingFor}.", h.Last.Message);
        Assert.Null(h.Last.ActionText);
        Assert.Null(h.Last.Action);
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>A session or a flash started while the update downloaded holds the restart.</summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging)]
    [InlineData(UpdateBlocker.FirmwareUpdate)]
    [InlineData(UpdateBlocker.SdCardTransfer)]
    public async Task WorkStartedDuringDownload_HoldsTheRestart(UpdateBlocker blocker)
    {
        var h = new Harness();
        h.Service.OnDownload = (_, _) => h.Blocker = blocker;

        await h.DownloadAsync();

        Assert.StartsWith("DAQiFi 3.4.0 is ready. Restart when", h.Last.Message);
        Assert.Null(h.Last.Action);
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>
    /// #348: when the work ends, "Restart to update" comes back by itself, with no new check, and
    /// the button then installs. The restart never happens by itself. Looks that see no change
    /// replace nothing, so the notification does not flicker.
    /// </summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging)]
    [InlineData(UpdateBlocker.FirmwareUpdate)]
    [InlineData(UpdateBlocker.SdCardTransfer)]
    public async Task ReadyWhileBusy_GetsItsButtonBack_WhenTheWorkEnds(UpdateBlocker blocker)
    {
        var h = new Harness { Blocker = blocker };
        await h.DownloadAsync();
        h.OnDelay = n => { if (n == 3) { h.Blocker = UpdateBlocker.None; } };

        await h.WatchAsync(looks: 5);

        Assert.Equal(3, h.Shown.Count);             // downloading, ready without button, ready
        Assert.Equal(READY, h.Last.Message);
        Assert.Equal(RESTART, h.Last.ActionText);
        Assert.Equal(0, h.Service.Checks);
        Assert.Equal(0, h.Service.Applies);

        await h.ClickAsync();

        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>#348: work that starts while "ready" is shown takes the restart button away until the work ends.</summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging, "when logging stops")]
    [InlineData(UpdateBlocker.FirmwareUpdate, "when the firmware update finishes")]
    [InlineData(UpdateBlocker.SdCardTransfer, "when the SD card transfer finishes")]
    public async Task WorkStartedWhileReady_TakesTheButtonAway_UntilItEnds(UpdateBlocker blocker, string waitingFor)
    {
        var h = new Harness();
        await h.DownloadAsync();
        h.OnDelay = n =>
        {
            if (n == 2) { h.Blocker = blocker; }
            if (n == 4) { h.Blocker = UpdateBlocker.None; }
        };

        await h.WatchAsync(looks: 5);

        Assert.Equal(
            ["Downloading DAQiFi 3.4.0... 0%", READY, $"DAQiFi 3.4.0 is ready. Restart {waitingFor}.", READY],
            h.Messages);
        Assert.Equal(["Cancel", RESTART, null, RESTART], h.Shown.Select(n => n!.ActionText));
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>
    /// #348 review: work starts while "ready" shows, and the user clicks before the watch looks
    /// again. The click is refused: the notification shows the reason with no button, and nothing is
    /// installed. When the work ends, the button comes back and installs.
    /// </summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging, "when logging stops")]
    [InlineData(UpdateBlocker.FirmwareUpdate, "when the firmware update finishes")]
    [InlineData(UpdateBlocker.SdCardTransfer, "when the SD card transfer finishes")]
    public async Task RestartClickBeforeTheNextLook_WhileWorkRuns_IsRefused(UpdateBlocker blocker, string waitingFor)
    {
        var h = new Harness();
        await h.DownloadAsync();
        Assert.Equal(RESTART, h.Last.ActionText);

        h.Blocker = blocker;
        await h.ClickAsync();

        Assert.Equal($"DAQiFi 3.4.0 is ready. Restart {waitingFor}.", h.Last.Message);
        Assert.Null(h.Last.ActionText);
        Assert.Equal(0, h.Service.Applies);

        h.OnDelay = _ => h.Blocker = UpdateBlocker.None;
        await h.WatchAsync(looks: 1);
        Assert.Equal(RESTART, h.Last.ActionText);

        await h.ClickAsync();

        Assert.Equal(1, h.Service.Downloads);
        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>
    /// #348 review: "Try again" after a failed install leads to a restart too, so a click on it
    /// before the watch looks again is refused in the same way.
    /// </summary>
    [Fact]
    public async Task RetryAfterAFailedInstall_WhileWorkRuns_IsRefused()
    {
        var h = new Harness();
        h.Service.ApplyFailure = new InvalidOperationException("locked");
        await h.DownloadAsync();
        await h.ClickAsync();
        Assert.Equal("Try again", h.Last.ActionText);

        h.Blocker = UpdateBlocker.Logging;
        await h.ClickAsync();

        Assert.Equal("Couldn't install the update. Try again when logging stops.", h.Last.Message);
        Assert.Null(h.Last.ActionText);
        Assert.Equal(1, h.Service.Applies);   // only the first, failed, attempt
    }

    /// <summary>While a download shows its own notification, the watch leaves it alone.</summary>
    [Fact]
    public async Task Watch_LeavesADownloadUnderWayAlone()
    {
        var h = new Harness();
        var downloaded = new TaskCompletionSource();
        h.Service.DownloadBody = _ => downloaded.Task;
        await h.Notifier.CheckAsync();
        var download = h.DownloadAsync();
        var shownBefore = h.Shown.Count;

        h.OnDelay = _ => h.Blocker = UpdateBlocker.Logging;
        await h.WatchAsync(looks: 3);

        Assert.Equal(shownBefore, h.Shown.Count);
        Assert.Equal("Downloading DAQiFi 3.4.0... 0%", h.Last.Message);

        downloaded.SetResult();
        await download;

        Assert.Equal("DAQiFi 3.4.0 is ready. Restart when logging stops.", h.Last.Message);
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>"Try again" after a failed download only downloads, so it is offered while work runs.</summary>
    [Fact]
    public async Task FailedDownload_WhileWorkRuns_StillOffersARetry()
    {
        var h = new Harness();
        h.Service.DownloadFailure = new IOException("disk full");
        h.Service.OnDownload = (_, _) => h.Blocker = UpdateBlocker.SdCardTransfer;

        await h.DownloadAsync();

        Assert.Equal("Couldn't download the update.", h.Last.Message);
        Assert.Equal("Try again", h.Last.ActionText);
    }

    /// <summary>When the check cannot tell, "ready" has no button until it can; the failure is logged once.</summary>
    [Fact]
    public async Task BlockerCheckThrowing_ReadyHasNoButton_AndLogsOnce()
    {
        var h = new Harness();
        var throwing = true;
        h.BlockerSource = () => throwing ? throw new InvalidOperationException("view gone") : UpdateBlocker.None;

        await h.DownloadAsync();

        Assert.Equal("DAQiFi 3.4.0 is ready. Restart when DAQiFi is idle.", h.Last.Message);
        Assert.Null(h.Last.ActionText);

        h.OnDelay = n => { if (n == 3) { throwing = false; } };
        await h.WatchAsync(looks: 3);

        Assert.Equal(RESTART, h.Last.ActionText);
        Assert.Single(h.Logger.Warnings);
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>The text of a held restart stays one short line, for every reason it can give.</summary>
    [Theory]
    [InlineData(UpdateBlocker.Logging)]
    [InlineData(UpdateBlocker.FirmwareUpdate)]
    [InlineData(UpdateBlocker.SdCardTransfer)]
    [InlineData(UpdateBlocker.OtherWindowOpen)]
    [InlineData(UpdateBlocker.LoggedDataBusy)]
    [InlineData(UpdateBlocker.ProfileBeingEdited)]
    public async Task ReadyWhileBusy_IsOneShortLine(UpdateBlocker blocker)
    {
        var h = new Harness { Blocker = blocker };

        await h.DownloadAsync();

        Assert.StartsWith("DAQiFi 3.4.0 is ready. Restart when ", h.Last.Message);
        Assert.DoesNotContain('\n', h.Last.Message);
        Assert.True(h.Last.Message.Length <= 90, h.Last.Message);
    }

    /// <summary>Messages stay one short line, through a held restart, another copy, and the install.</summary>
    [Fact]
    public async Task EveryMessage_IsOneShortLine()
    {
        var marker = TempMarker();
        marker.Remember("3.4.0");
        var h = new Harness { Blocker = UpdateBlocker.OtherWindowOpen, Attempts = marker };
        h.Service.ApplyNeedsElevation = true;
        h.Service.InUseElsewhere = true;

        await h.Notifier.CheckAsync();            // the longest offer: "wasn't installed"
        await h.DownloadAsync();
        h.OnDelay = _ => h.Blocker = UpdateBlocker.None;
        await h.WatchAsync(looks: 1);
        await h.ClickAsync();                     // another copy runs
        h.Service.InUseElsewhere = false;
        await h.ClickAsync();

        Assert.Equal(1, h.Service.Applies);
        Assert.All(h.Messages, message =>
        {
            Assert.DoesNotContain('\n', message);
            Assert.True(message.Length <= 90, message);
        });
    }

    /// <summary>
    /// Applying ends every process in the install folder, in every session. On a shared PC that is
    /// another person's recording (#503 review), so a click while another copy runs restarts
    /// nothing, says why, and keeps the button so the user can try again once it is closed.
    /// </summary>
    [Fact]
    public async Task OtherCopyRunning_RestartsNothing_UntilItCloses()
    {
        var h = new Harness();
        await h.DownloadAsync();
        h.Service.InUseElsewhere = true;

        await h.ClickAsync();

        Assert.Equal(0, h.Service.Applies);
        Assert.Equal("DAQiFi 3.4.0 is ready. Restart when other copies of DAQiFi on this PC are closed.", h.Last.Message);
        Assert.Equal(RESTART, h.Last.ActionText);

        h.Service.InUseElsewhere = false;
        await h.ClickAsync();

        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>When the other-copies check cannot tell, nothing restarts; the failure is logged once.</summary>
    [Fact]
    public async Task OtherCopyCheckFailing_RestartsNothing_AndLogsOnce()
    {
        var h = new Harness();
        await h.DownloadAsync();
        h.Service.InUseCheckFailure = new InvalidOperationException("no install folder");

        await h.ClickAsync();
        await h.ClickAsync();

        Assert.Equal(0, h.Service.Applies);
        Assert.Equal("DAQiFi 3.4.0 is ready. Restart when DAQiFi is idle.", h.Last.Message);
        Assert.Single(h.Logger.Warnings);

        h.Service.InUseCheckFailure = null;
        await h.ClickAsync();

        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>This process's own reason is the one the user can act on here, so it is the one shown.</summary>
    [Fact]
    public async Task OwnBlocker_IsReportedBeforeAnotherCopy()
    {
        var h = new Harness();
        await h.DownloadAsync();
        h.Blocker = UpdateBlocker.Logging;
        h.Service.InUseElsewhere = true;

        await h.ClickAsync();

        Assert.Contains("logging stops", h.Last.Message);
        Assert.DoesNotContain(h.Messages, m => m.Contains("other copies"));
        Assert.Equal(0, h.Service.Applies);
    }

    /// <summary>A second click while a download runs must not download twice.</summary>
    [Fact]
    public async Task SecondDownloadWhileOneRuns_DoesNothing()
    {
        var h = new Harness();
        var downloaded = new TaskCompletionSource();
        h.Service.DownloadBody = _ => downloaded.Task;

        var first = h.DownloadAsync();
        var second = h.DownloadAsync();
        downloaded.SetResult();
        await first;
        await second;

        Assert.Equal(1, h.Service.Downloads);
    }

    [Fact]
    public async Task AllUsersInstall_AsksAtTheRestart_AndDeclineInstallsNothing()
    {
        var h = new Harness { ConfirmAnswer = false };
        h.Service.ApplyNeedsElevation = true;
        await h.DownloadAsync();
        Assert.Empty(h.Confirms);                 // the download asks nothing

        await h.ClickAsync();

        Assert.Contains("administrator", Assert.Single(h.Confirms));
        Assert.Equal(0, h.Service.Applies);
        Assert.Equal(RESTART, h.Last.ActionText); // still ready, to restart later
    }

    [Fact]
    public async Task AllUsersInstall_ConfirmedGoesAhead()
    {
        var h = new Harness();
        h.Service.ApplyNeedsElevation = true;
        await h.DownloadAsync();

        await h.ClickAsync();

        Assert.Single(h.Confirms);
        Assert.Equal(1, h.Service.Applies);
    }

    /// <summary>
    /// The question says when the administrator prompt comes: the updater shows it after DAQiFi
    /// closes, not at once (#503 review).
    /// </summary>
    [Fact]
    public async Task AllUsersInstall_SaysThePromptComesWhenDaqifiRestarts()
    {
        var h = new Harness();
        h.Service.ApplyNeedsElevation = true;
        await h.DownloadAsync();

        await h.ClickAsync();

        Assert.Equal(
            "Installing needs administrator permission. Windows will ask when DAQiFi restarts.",
            Assert.Single(h.Confirms));
    }

    /// <summary>Work that starts while the question is open is seen after the answer: nothing installs.</summary>
    [Fact]
    public async Task WorkStartedWhileTheQuestionIsOpen_InstallsNothing()
    {
        var h = new Harness();
        h.Service.ApplyNeedsElevation = true;
        await h.DownloadAsync();
        // A flash starts while the question is on screen: the gate reads busy once a question was asked.
        h.BlockerSource = () => h.Confirms.Count > 0 ? UpdateBlocker.FirmwareUpdate : UpdateBlocker.None;

        await h.ClickAsync();

        Assert.Single(h.Confirms);
        Assert.Equal(0, h.Service.Applies);
        Assert.Equal("DAQiFi 3.4.0 is ready. Restart when the firmware update finishes.", h.Last.Message);
    }

    [Fact]
    public async Task FailedDownload_SaysSoInOneLine_AndOffersARetry()
    {
        var h = new Harness();
        h.Service.DownloadFailure = new IOException("disk full");

        await h.DownloadAsync();

        Assert.Equal(0, h.Service.Applies);
        Assert.Equal("Couldn't download the update.", h.Last.Message);
        Assert.Equal("Try again", h.Last.ActionText);
        Assert.Contains("Downloading", Assert.Single(h.Logger.Warnings));

        h.Service.DownloadFailure = null;
        await h.ClickAsync();

        Assert.Equal(2, h.Service.Downloads);
        Assert.Equal(READY, h.Last.Message);
    }

    [Fact]
    public async Task FailedApply_SaysSoInOneLine_AndOffersARetry()
    {
        var h = new Harness();
        h.Service.ApplyFailure = new InvalidOperationException("locked");
        await h.DownloadAsync();

        await h.ClickAsync();

        Assert.Equal("Couldn't install the update.", h.Last.Message);
        Assert.Equal("Try again", h.Last.ActionText);
        Assert.Single(h.Logger.Errors);

        h.Service.ApplyFailure = null;
        await h.ClickAsync();

        Assert.Equal(2, h.Service.Applies);
        Assert.Equal(1, h.Service.Downloads);     // the retry installs what was downloaded
    }

    // ---- A later check (#346 review) ----

    /// <summary>An app left open across a release offers the newer version in place of the older one.</summary>
    [Fact]
    public async Task LaterCheck_NewerVersion_ReplacesTheOffer()
    {
        var h = new Harness();
        await h.Notifier.CheckAsync();

        h.Service.Offer = new("3.4.1", null);
        var shown = await h.Notifier.CheckAsync();

        Assert.Equal("DAQiFi 3.4.1 is available.", shown!.Message);
        Assert.Same(shown, h.Shown.Last());
    }

    /// <summary>A version taken off the feed is no longer offered, downloaded or not.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterCheck_WithdrawnVersion_TakesTheOfferDown(bool downloaded)
    {
        var h = new Harness();
        await h.Notifier.CheckAsync();
        if (downloaded) { await h.DownloadAsync(); }

        h.Service.Offer = null;
        Assert.Null(await h.Notifier.CheckAsync());

        Assert.Null(h.Shown.Last());
        Assert.False(h.Notifier.HasOffer);
    }

    /// <summary>A version already downloaded stays ready: it is not offered for download again.</summary>
    [Fact]
    public async Task LaterCheck_SameVersionDownloaded_StaysReady()
    {
        var h = new Harness();
        await h.Notifier.CheckAsync();
        await h.DownloadAsync();
        var shownBefore = h.Shown.Count;

        var shown = await h.Notifier.CheckAsync();

        Assert.Equal(READY, shown!.Message);
        Assert.Equal(shownBefore, h.Shown.Count);
        Assert.Equal(1, h.Service.Downloads);
    }

    /// <summary>A newer version than the one downloaded is offered for download in its place.</summary>
    [Fact]
    public async Task LaterCheck_NewerThanTheDownload_IsOfferedForDownload()
    {
        var h = new Harness();
        await h.Notifier.CheckAsync();
        await h.DownloadAsync();

        h.Service.Offer = new("3.4.1", null);
        var shown = await h.Notifier.CheckAsync();

        Assert.Equal("DAQiFi 3.4.1 is available.", shown!.Message);
        Assert.Equal(DOWNLOAD, shown.ActionText);
    }

    /// <summary>Offline after an offer: the offer stays as it was.</summary>
    [Fact]
    public async Task LaterCheck_Failing_LeavesTheOfferAlone()
    {
        var h = new Harness();
        await h.Notifier.CheckAsync();
        var shownBefore = h.Shown.Count;

        h.Service.CheckFailure = new HttpRequestException("offline");
        Assert.Null(await h.Notifier.CheckAsync());

        Assert.Equal(shownBefore, h.Shown.Count);
        Assert.True(h.Notifier.HasOffer);
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
        await before.DownloadAsync();
        await before.ClickAsync();
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
