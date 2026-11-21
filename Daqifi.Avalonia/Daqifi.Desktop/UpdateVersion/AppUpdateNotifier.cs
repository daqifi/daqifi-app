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
/// Turns an available update into a notification, downloads it when the user asks, and installs it
/// when the user asks again. Replaces the startup check that asked the WPF app's repository for a
/// version it could never parse (#345).
/// </summary>
/// <remarks>
/// Nothing is downloaded until the user clicks "Download update" (#341: notify only, download on
/// click). The update's state then lives in that one notification, replaced at each step: the
/// download's percentage, "ready", or what went wrong. Each step has a button, and Cancel stops the
/// download. A finished download does not restart the app: the notification says the update is
/// ready, and "Restart to update" installs it when the user chooses (#346 review). A later check
/// replaces an offer with a newer version, or takes it down when the feed no longer has it.
/// <para>
/// Installing restarts the app, so "Restart to update" is never offered while the restart would cut
/// something off (#348): a logging session, a firmware write, an SD card transfer, or any other
/// <see cref="UpdateBlocker"/>. The notification says what the restart waits for and has no button,
/// and <see cref="WatchAsync"/> puts the button back when that work ends. A click that comes after
/// the work starts but before the watch looks again is refused in the same way. Downloading stops
/// nothing, so "Download update" is offered at any time.
/// </para>
/// </remarks>
public sealed class AppUpdateNotifier
{
    /// <summary>
    /// How often <see cref="WatchAsync"/> looks again. Short, because here the user waits for the
    /// button to come back, and each look only reads a few flags.
    /// </summary>
    public static readonly TimeSpan OfferRecheckInterval = TimeSpan.FromSeconds(2);

    private const string DOWNLOAD = "Download update";
    private const string RESTART = "Restart to update";
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

    // The notification with a button for the user, while one is shown. Null while an install step
    // shows its own (the download's progress), and while nothing is shown.
    private Offered? _offered;

    // The version downloaded and ready to install, or null.
    private string? _readyVersion;

    // What was shown last, or null when nothing is.
    private Notifications? _shown;

    /// <param name="service">The update service for this head.</param>
    /// <param name="blocker">What, if anything, installing would interrupt right now.</param>
    /// <param name="confirm">Asks a yes/no question (message, title); true means go ahead.</param>
    /// <param name="show">Shows the update's notification in place of the previous one; <c>null</c> removes it. May be called from any thread.</param>
    /// <param name="logger">Where failures are recorded.</param>
    /// <param name="delay">Waits between the looks of <see cref="WatchAsync"/>; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test supplies one.</param>
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

    /// <summary>True when the last <see cref="CheckAsync"/> got no answer from the update service (#346).</summary>
    public bool LastCheckFailed { get; private set; }

    /// <summary>
    /// True while the notification shown waits for the user: an offer, a ready update, or a failure
    /// to try again. False while a download runs, and while nothing is shown. A new check is safe
    /// only when this is true or nothing is shown, because it replaces the notification.
    /// </summary>
    public bool HasOffer => _offered is not null;

    /// <summary>
    /// Checks for a newer version and shows what it found. A version on offer is replaced by a newer
    /// one, and taken down when the feed no longer offers any (#346 review). A version already
    /// downloaded stays ready. Returns the notification shown, or <c>null</c> when updates are
    /// unsupported, this copy is current, or the check failed; a failed check changes nothing shown.
    /// Never throws. Call <see cref="WatchAsync"/> after an offer, so that "Restart to update"
    /// follows what installing would cut off. Do not call while a download runs (<see cref="HasOffer"/>).
    /// </summary>
    public async Task<Notifications?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_service.Support != AppUpdateSupport.Supported) { return null; }

        AppUpdateOffer? offer;
        try
        {
            offer = await _service.CheckAsync(cancellationToken);
            LastCheckFailed = false;
        }
        // A timed-out request also surfaces as a cancellation; only the caller's own cancel propagates.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Offline is normal for this app (field and lab networks), so this is not an error.
            _logger.Warning(ex, "Checking for an app update failed");
            LastCheckFailed = true;
            return null;
        }

        // Taken only after a check that worked: offline, the next check can still tell.
        var attempted = _attempts?.Take();
        if (offer is null)
        {
            // The feed no longer has what is offered, for example a release taken back.
            if (_offered is not null)
            {
                _offered = null;
                _readyVersion = null;
                _shown = null;
                _show(null);
            }

            return null;
        }

        // Already downloaded: it stays ready to install, and is not offered for download again.
        if (offer.Version == _readyVersion) { return _shown; }

        _readyVersion = null;

        // The same version still on offer means the last install did not happen. On an install for
        // all users that is nearly always the administrator prompt: cancelled, or no one there who
        // can approve it.
        return attempted != offer.Version
            ? ShowAvailable(offer)
            : ShowOffer(
                Offer(offer, _service.ApplyNeedsElevation
                    ? $"DAQiFi {offer.Version} wasn't installed. An administrator must approve the update."
                    : $"DAQiFi {offer.Version} wasn't installed.", TRY_AGAIN),
                whileBusy: null);
    }

    /// <summary>
    /// Keeps "Restart to update" in step with what installing would cut off (#348). While the app
    /// logs, writes firmware, moves files from an SD card, or does other work that
    /// <see cref="UpdateBlocker"/> names, the notification says what the restart waits for and has no
    /// button. When the work ends, the button comes back by itself, with no new check. Looks again
    /// every <see cref="OfferRecheckInterval"/> until <paramref name="cancellationToken"/> is
    /// cancelled. It changes only the notification: it never downloads or installs.
    /// </summary>
    public async Task WatchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                await _delay(OfferRecheckInterval, cancellationToken);

                // Nothing to keep in step while a download shows its own notification, or for an
                // offer whose button only downloads.
                if (_offered is not { WhileBusy: not null } offered) { continue; }

                var waitingFor = OwnWaitingFor();
                if (waitingFor != offered.ShownWaitingFor) { Show(offered, waitingFor); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Downloads <paramref name="offer"/>, then shows it ready with "Restart to update". Cancel on the
    /// notification stops the download. A second call while one is under way does nothing.
    /// </summary>
    public Task InstallAsync(AppUpdateOffer offer) => RunAsync(offer, DownloadAsync);

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

    private async Task DownloadAsync(AppUpdateOffer offer, CancellationToken cancellationToken)
    {
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
            ShowOffer(Offer(offer, "Couldn't download the update.", TRY_AGAIN), whileBusy: null);
            return;
        }

        _readyVersion = offer.Version;
        ShowReady(offer, $"DAQiFi {offer.Version} is ready.");
    }

    /// <summary>
    /// Installs the downloaded <paramref name="offer"/> and restarts, on the user's click. Asked again
    /// here, right before applying: work may have started since the button was shown, and other
    /// copies of DAQiFi are looked for only now.
    /// </summary>
    private async Task ApplyAsync(AppUpdateOffer offer, CancellationToken cancellationToken)
    {
        // The updater closes DAQiFi before Windows shows the administrator prompt, so the user hears
        // about the prompt first, and can decide to restart later.
        if (_service.ApplyNeedsElevation && !await _confirm(
                "Installing needs administrator permission. Windows will ask when DAQiFi restarts.", "Update"))
        {
            return;
        }

        // This process's own work: the button goes until the work ends (#348), as the watch does.
        if (OwnWaitingFor() is not null)
        {
            ShowReady(offer, $"DAQiFi {offer.Version} is ready.");
            return;
        }

        // Other copies, which the updater closes in every user's session. The button stays, so the
        // user can try again once they are closed.
        if (Checked(() => _service.IsInstallInUseElsewhere() ? OTHER_COPIES : null) is { } others)
        {
            ShowReady(offer, $"DAQiFi {offer.Version} is ready. Restart {others}.");
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
            ShowOffer(Ready(offer, "Couldn't install the update.", TRY_AGAIN), "Couldn't install the update. Try again");
        }
    }

    private Notifications ShowAvailable(AppUpdateOffer offer) =>
        ShowOffer(Offer(offer, $"DAQiFi {offer.Version} is available.", DOWNLOAD), whileBusy: null);

    private Notifications ShowReady(AppUpdateOffer offer, string message) =>
        ShowOffer(Ready(offer, message, RESTART), $"DAQiFi {offer.Version} is ready. Restart");

    /// <summary>
    /// Shows a notification with a button for the user. When the button would restart, it is never
    /// shown while the restart would cut something off (#348): what is shown in its place says what
    /// the restart waits for and has no button, and <see cref="WatchAsync"/> swaps the two as that
    /// work starts and ends. Returns what is shown.
    /// </summary>
    /// <param name="available">The notification with the button.</param>
    /// <param name="whileBusy">The start of the text shown in its place, as in "... Restart"; the reason follows, as in "when logging stops.". <c>null</c> for a button that does not restart, which is always shown.</param>
    private Notifications ShowOffer(Notifications available, string? whileBusy)
    {
        var offered = new Offered(available, whileBusy);
        _offered = offered;
        return Show(offered, whileBusy is null ? null : OwnWaitingFor());
    }

    private Notifications Show(Offered offered, string? waitingFor)
    {
        offered.ShownWaitingFor = waitingFor;
        var shown = waitingFor is null
            ? offered.Available
            : new Notifications { Message = $"{offered.WhileBusy} {waitingFor}." };
        _shown = shown;
        _show(shown);
        return shown;
    }

    /// <summary>Shows a step under way. It replaces the offer, so the watch leaves it alone.</summary>
    private void ShowBusy(string message)
    {
        _offered = null;
        var busy = Busy(message);
        _shown = busy;
        _show(busy);
    }

    // An offer whose button downloads: nothing is interrupted, so it is never refused.
    private Notifications Offer(AppUpdateOffer offer, string message, string actionText) => new()
    {
        Message = message,
        ActionText = actionText,
        Action = new AsyncRelayCommand(() => InstallAsync(offer)),
    };

    // A downloaded update: the click installs it and restarts, unless something started again.
    private Notifications Ready(AppUpdateOffer offer, string message, string actionText) => new()
    {
        Message = message,
        ActionText = actionText,
        Action = Click(() => RunAsync(offer, ApplyAsync)),
    };

    /// <summary>
    /// The command of a button that would restart. The watch takes the button away at its next look
    /// after work starts, so a click can come before that look. Such a click is refused (#348
    /// review): the notification shows what the restart waits for, with no button, and nothing is
    /// asked or installed.
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
    /// What the restart would cut off in this process, as the end of "Restart ...", or <c>null</c>
    /// when nothing. A ready update follows only this: other copies are looked for when the user
    /// clicks, because listing every process on the PC every few seconds, for as long as an update
    /// is ready, costs more than it tells. If the check fails the restart waits, since closing
    /// someone's recording is worse than installing later; the failure is logged once.
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
    /// A notification with a button for the user, and what is shown in its place while the button
    /// would cut something off. <see cref="WhileBusy"/> is <c>null</c> for a button that is always
    /// shown. <see cref="ShownWaitingFor"/> is what the notification on show waits for, or
    /// <c>null</c> when it is <see cref="Available"/>.
    /// </summary>
    private sealed class Offered(Notifications available, string? whileBusy)
    {
        public Notifications Available { get; } = available;

        public string? WhileBusy { get; } = whileBusy;

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
