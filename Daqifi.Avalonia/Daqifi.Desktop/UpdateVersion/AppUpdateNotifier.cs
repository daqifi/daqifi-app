using CommunityToolkit.Mvvm.Input;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Models;

namespace Daqifi.Desktop.UpdateVersion;

/// <summary>Work that installing an update would cut off, since installing restarts the app.</summary>
public enum UpdateBlocker
{
    /// <summary>Nothing running; installing is safe.</summary>
    None,

    /// <summary>A logging session is recording (#348).</summary>
    Logging,

    /// <summary>A device's firmware is being written. Cutting that off can leave the device unusable.</summary>
    FirmwareUpdate,

    /// <summary>Another DAQiFi window (an export, a firmware or connection dialog) is open.</summary>
    OtherWindowOpen,

    /// <summary>Files are being read from a device's SD card, or imported from it.</summary>
    SdCardTransfer,

    /// <summary>Logged data is being imported from a file, or sessions are being deleted.</summary>
    LoggedDataBusy,

    /// <summary>A profile is open for editing. Its changes are saved only when the editor closes.</summary>
    ProfileBeingEdited,
}

/// <summary>
/// Turns an available update into a notification, and installs it when the user asks. Replaces the
/// startup check that asked the WPF app's repository for a version it could never parse (#345).
/// </summary>
/// <remarks>
/// Nothing is downloaded until the user clicks Install (#341: notify only, download on click). The
/// update's state then lives in that one notification, replaced at each step: the download's
/// percentage, what a waiting update waits for, or what went wrong. Each step has a button, and
/// Cancel stops the download or the wait. Installing restarts the app, so when something is running
/// that the restart would cut off, the update waits for it and then installs on its own. On an
/// install for all users it does not: the wait ends with "Update ready." and a button, because the
/// administrator prompt must come only when a person is there to answer it.
/// <para>
/// A button that would install is never offered while the restart would cut something off (#348):
/// a logging session, a firmware write, an SD card transfer, or any other <see cref="UpdateBlocker"/>.
/// The notification says what the install waits for and has no button, and <see cref="WatchAsync"/>
/// puts the button back when that work ends. A click that comes after the work starts but before
/// the watch looks again is refused in the same way.
/// </para>
/// </remarks>
public sealed class AppUpdateNotifier
{
    /// <summary>How often a waiting update looks again at what it is waiting for.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often <see cref="WatchAsync"/> looks again. Shorter than <see cref="RecheckInterval"/>,
    /// because here the user waits for the button to come back, and each look only reads a few flags.
    /// </summary>
    public static readonly TimeSpan OfferRecheckInterval = TimeSpan.FromSeconds(2);

    private const string INSTALL = "Install and restart";
    private const string TRY_AGAIN = "Try again";

    private readonly IAppUpdateService _service;
    private readonly Func<UpdateBlocker> _blocker;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<Notifications?> _show;
    private readonly IAppLogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly UpdateAttemptMarker? _attempts;
    private CancellationTokenSource? _installing;
    private bool _checkFailureLogged;

    // The notification whose button would install, while one is shown. Null while an install step
    // shows its own: its progress, or what it waits for.
    private Offered? _offered;

    /// <param name="service">The update service for this head.</param>
    /// <param name="blocker">What, if anything, installing would interrupt right now.</param>
    /// <param name="confirm">Asks a yes/no question (message, title); true means go ahead.</param>
    /// <param name="show">Shows the update's notification in place of the previous one; <c>null</c> removes it. May be called from any thread.</param>
    /// <param name="logger">Where failures are recorded.</param>
    /// <param name="delay">Waits between checks while the update waits; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test supplies one.</param>
    /// <param name="attempts">Where the version being installed is remembered across the restart, so a copy that comes back without it can say so.</param>
    public AppUpdateNotifier(
        IAppUpdateService service,
        Func<UpdateBlocker> blocker,
        Func<string, string, Task<bool>> confirm,
        Action<Notifications?> show,
        IAppLogger logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        UpdateAttemptMarker? attempts = null)
    {
        _service = service;
        _blocker = blocker;
        _confirm = confirm;
        _show = show;
        _logger = logger;
        _delay = delay ?? Task.Delay;
        _attempts = attempts;
    }

    /// <summary>
    /// Checks for a newer version and, when there is one, shows its notification. Returns the
    /// notification shown, or <c>null</c> when updates are unsupported, this copy is current, or the
    /// check failed. Never throws. Call <see cref="WatchAsync"/> after an offer, so that its button
    /// follows what installing would cut off.
    /// </summary>
    public async Task<Notifications?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_service.Support != AppUpdateSupport.Supported) { return null; }

        AppUpdateOffer? offer;
        try
        {
            offer = await _service.CheckAsync(cancellationToken);
        }
        // A timed-out request also surfaces as a cancellation; only the caller's own cancel propagates.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Offline is normal for this app (field and lab networks), so this is not an error.
            _logger.Warning(ex, "Checking for an app update failed");
            return null;
        }

        // Taken only after a check that worked: offline, the next check can still tell.
        var attempted = _attempts?.Take();
        if (offer is null) { return null; }

        // The same version still on offer means the last install did not happen. On an install for
        // all users that is nearly always the administrator prompt: cancelled, or no one there who
        // can approve it.
        return attempted != offer.Version
            ? ShowAvailable(offer)
            : ShowOffer(
                Offer(offer, _service.ApplyNeedsElevation
                    ? $"DAQiFi {offer.Version} wasn't installed. An administrator must approve the update."
                    : $"DAQiFi {offer.Version} wasn't installed.", TRY_AGAIN),
                $"DAQiFi {offer.Version} wasn't installed. Try again");
    }

    /// <summary>
    /// Keeps an offered update in step with what installing would cut off (#348). While the app logs,
    /// writes firmware, moves files from an SD card, or does other work that <see cref="UpdateBlocker"/>
    /// names, the notification says what the install waits for and has no button. When the work ends,
    /// the offer and its button come back by themselves, with no new check. Looks again every
    /// <see cref="OfferRecheckInterval"/> until <paramref name="cancellationToken"/> is cancelled.
    /// It changes only the notification: it never downloads or installs.
    /// </summary>
    public async Task WatchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                await _delay(OfferRecheckInterval, cancellationToken);

                // Nothing to keep in step while an install step shows its own notification.
                if (_offered is not { } offered) { continue; }

                var waitingFor = OwnWaitingFor();
                if (waitingFor != offered.ShownWaitingFor) { Show(offered, waitingFor); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Downloads <paramref name="offer"/>, waits until nothing would be cut off, then installs it and
    /// restarts; on an install for all users, a wait ends with "Update ready." for the user to click.
    /// Cancel on the notification stops it at any point before the restart. A second call while one
    /// is under way does nothing.
    /// </summary>
    public Task InstallAsync(AppUpdateOffer offer) => RunAsync(offer, DownloadAndInstallAsync);

    /// <summary>Runs one install step that Cancel can stop. A second step while one is under way does nothing.</summary>
    private async Task RunAsync(AppUpdateOffer offer, Func<AppUpdateOffer, CancellationToken, Task> step)
    {
        if (_installing is not null) { return; }
        using var cancel = new CancellationTokenSource();
        _installing = cancel;
        try
        {
            await step(offer, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            ShowAvailable(offer);
        }
        finally
        {
            _installing = null;
        }
    }

    private async Task DownloadAndInstallAsync(AppUpdateOffer offer, CancellationToken cancellationToken)
    {
        if (_service.ApplyNeedsElevation && !await _confirm(
                "Installing needs administrator permission. Windows will ask when DAQiFi restarts.", "Update"))
        {
            return;
        }

        // Downloading interrupts nothing, so it starts at once even while something is running.
        // Progress arrives on the download's thread; ShowBusy below has already taken the offer down.
        var progress = new PercentProgress(percent => _show(Busy($"Downloading DAQiFi {offer.Version}... {percent}%")));
        ShowBusy($"Downloading DAQiFi {offer.Version}... 0%");
        try
        {
            await _service.DownloadAsync(offer, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.Warning(ex, $"Downloading app update {offer.Version} failed");
            ShowOffer(Offer(offer, "Couldn't download the update.", TRY_AGAIN), "Couldn't download the update. Try again");
            return;
        }

        await WaitThenApplyAsync(offer, cancellationToken);
    }

    /// <summary>
    /// Waits until nothing would be cut off, then applies. On an install for all users, a wait ends
    /// with "Update ready." instead, and the user's click on it applies.
    /// </summary>
    private async Task WaitThenApplyAsync(AppUpdateOffer offer, CancellationToken cancellationToken)
    {
        var asksFirst = _service.ApplyNeedsElevation;
        var waited = false;

        // Asked again right before applying: anything may have started while waiting.
        var waitingFor = WaitingFor();
        while (waitingFor is not null)
        {
            waited = true;
            ShowBusy(asksFirst ? $"Update ready. Asks to install {waitingFor}." : $"Update ready. Installs {waitingFor}.");
            await _delay(RecheckInterval, cancellationToken);
            waitingFor = WaitingFor();
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The updater closes DAQiFi before Windows shows the administrator prompt. After a wait, which
        // can end hours later, no one may be at the PC, or only a standard user who cannot approve it;
        // DAQiFi would then stay closed while the prompt waits (#503 review). So the user decides.
        if (waited && asksFirst)
        {
            ShowOffer(Ready(offer), "Update ready. Install it");
            return;
        }

        // Known gap, accepted for now (#509): on an install for all users, the updater's administrator
        // prompt comes after this last check. A copy another user starts while that prompt is open
        // is not seen here, and the updater closes it once the prompt is approved.
        try
        {
            _attempts?.Remember(offer.Version);
            _service.ApplyAndRestart(offer);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Applying app update {offer.Version} failed");
            ShowOffer(Offer(offer, "Couldn't install the update.", TRY_AGAIN), "Couldn't install the update. Try again");
        }
    }

    private Notifications ShowAvailable(AppUpdateOffer offer) => ShowOffer(
        Offer(offer, $"DAQiFi {offer.Version} is available.", INSTALL),
        $"DAQiFi {offer.Version} is available. Install it");

    /// <summary>
    /// Shows a notification whose button would install. While installing would cut something off,
    /// what is shown in its place says what the install waits for and has no button (#348).
    /// <see cref="WatchAsync"/> then swaps the two as that work starts and ends. Returns what is shown.
    /// </summary>
    /// <param name="available">The notification with the button.</param>
    /// <param name="whileBusy">The start of the text shown in its place; the reason follows, as in "when logging stops.".</param>
    private Notifications ShowOffer(Notifications available, string whileBusy)
    {
        var offered = new Offered(available, whileBusy);
        _offered = offered;
        return Show(offered, OwnWaitingFor());
    }

    private Notifications Show(Offered offered, string? waitingFor)
    {
        offered.ShownWaitingFor = waitingFor;
        var shown = waitingFor is null
            ? offered.Available
            : new Notifications { Message = $"{offered.WhileBusy} {waitingFor}." };
        _show(shown);
        return shown;
    }

    /// <summary>Shows a step under way. It replaces the offer, so the watch leaves it alone.</summary>
    private void ShowBusy(string message)
    {
        _offered = null;
        _show(Busy(message));
    }

    private Notifications Offer(AppUpdateOffer offer, string message, string actionText) => new()
    {
        Message = message,
        ActionText = actionText,
        Action = Click(() => InstallAsync(offer)),
    };

    // A downloaded update for all users that waited: the click applies it, unless something started again.
    private Notifications Ready(AppUpdateOffer offer) => new()
    {
        Message = "Update ready.",
        ActionText = INSTALL,
        Action = Click(() => RunAsync(offer, WaitThenApplyAsync)),
    };

    /// <summary>
    /// The command of a button that would install. The watch takes the button away at its next look
    /// after work starts, so a click can come before that look. Such a click is refused (#348
    /// review): the offer shows what the install waits for, with no button, and nothing is asked,
    /// downloaded or installed.
    /// </summary>
    private AsyncRelayCommand Click(Func<Task> install) => new(() =>
    {
        if (OwnWaitingFor() is not { } waitingFor) { return install(); }

        if (_offered is { } offered) { Show(offered, waitingFor); }
        return Task.CompletedTask;
    });

    // A step under way: its only button stops it.
    private Notifications Busy(string message) => new()
    {
        Message = message,
        ActionText = "Cancel",
        Action = new RelayCommand(() => _installing?.Cancel()),
    };

    /// <summary>
    /// What the restart would cut off, as the end of "Installs ...", or <c>null</c> when nothing.
    /// This process's own work comes first, then other copies, which the updater also closes, in
    /// every user's session. If either check fails the update keeps waiting, since closing someone's
    /// recording is worse than installing later; the failure is logged once.
    /// </summary>
    private string? WaitingFor() =>
        OwnWaitingFor() ?? Checked(() => _service.IsInstallInUseElsewhere() ? OTHER_COPIES : null);

    /// <summary>
    /// What the restart would cut off in this process, or <c>null</c> when nothing. An offer follows
    /// only this: other copies are looked for when installing, because listing every process on the
    /// PC every few seconds, for as long as an update is offered, costs more than it tells.
    /// </summary>
    private string? OwnWaitingFor() => Checked(() => _blocker() switch
    {
        UpdateBlocker.Logging => "when logging stops",
        UpdateBlocker.FirmwareUpdate => "when the firmware update finishes",
        UpdateBlocker.OtherWindowOpen => "when DAQiFi's other windows are closed",
        UpdateBlocker.SdCardTransfer => "when the SD card transfer finishes",
        UpdateBlocker.LoggedDataBusy => "when the import or delete finishes",
        UpdateBlocker.ProfileBeingEdited => "when the profile editor is closed",
        _ => null,
    });

    /// <summary>Runs one check. A check that throws reads as busy, and the first failure is logged.</summary>
    private string? Checked(Func<string?> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex)
        {
            if (!_checkFailureLogged)
            {
                _checkFailureLogged = true;
                _logger.Warning(ex, "Checking what an app update would interrupt failed");
            }

            return "when DAQiFi is idle";
        }
    }

    private const string OTHER_COPIES = "when other copies of DAQiFi on this PC are closed";

    /// <summary>
    /// A notification whose button would install, and what is shown in its place while installing
    /// would cut something off. <see cref="ShownWaitingFor"/> is what the notification on show waits
    /// for, or <c>null</c> when it is <see cref="Available"/>.
    /// </summary>
    private sealed class Offered(Notifications available, string whileBusy)
    {
        public Notifications Available { get; } = available;

        public string WhileBusy { get; } = whileBusy;

        public string? ShownWaitingFor { get; set; }
    }

    /// <summary>Reports only when the whole-number percentage changes, so the notification is not replaced on every byte.</summary>
    private sealed class PercentProgress(Action<int> report) : IProgress<int>
    {
        private int _last; // 0% is shown before the download starts

        public void Report(int value)
        {
            var percent = Math.Clamp(value, 0, 100);
            if (Interlocked.Exchange(ref _last, percent) != percent) { report(percent); }
        }
    }
}
