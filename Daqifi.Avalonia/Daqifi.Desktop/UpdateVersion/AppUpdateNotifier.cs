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
/// that the restart would cut off, the update waits for it and then installs on its own.
/// </remarks>
public sealed class AppUpdateNotifier
{
    /// <summary>How often a waiting update looks again at what it is waiting for.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(10);

    private readonly IAppUpdateService _service;
    private readonly Func<UpdateBlocker> _blocker;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<Notifications?> _show;
    private readonly IAppLogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _installing;
    private bool _checkFailureLogged;

    /// <param name="service">The update service for this head.</param>
    /// <param name="blocker">What, if anything, installing would interrupt right now.</param>
    /// <param name="confirm">Asks a yes/no question (message, title); true means go ahead.</param>
    /// <param name="show">Shows the update's notification in place of the previous one; <c>null</c> removes it. May be called from any thread.</param>
    /// <param name="logger">Where failures are recorded.</param>
    /// <param name="delay">Waits between checks while the update waits; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test supplies one.</param>
    public AppUpdateNotifier(
        IAppUpdateService service,
        Func<UpdateBlocker> blocker,
        Func<string, string, Task<bool>> confirm,
        Action<Notifications?> show,
        IAppLogger logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _service = service;
        _blocker = blocker;
        _confirm = confirm;
        _show = show;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Checks for a newer version and, when there is one, shows its notification. Returns that
    /// notification, or <c>null</c> when updates are unsupported, this copy is current, or the check
    /// failed. Never throws.
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

        if (offer is null) { return null; }

        var notification = Offer(offer, $"DAQiFi {offer.Version} is available.", "Install and restart");
        _show(notification);
        return notification;
    }

    /// <summary>
    /// Downloads <paramref name="offer"/>, waits until nothing would be cut off, then installs it and
    /// restarts. Cancel on the notification stops it at any point before the restart. A second call
    /// while one is under way does nothing.
    /// </summary>
    public async Task InstallAsync(AppUpdateOffer offer)
    {
        if (_installing is not null) { return; }
        using var cancel = new CancellationTokenSource();
        _installing = cancel;
        try
        {
            await DownloadAndInstallAsync(offer, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            _show(Offer(offer, $"DAQiFi {offer.Version} is available.", "Install and restart"));
        }
        finally
        {
            _installing = null;
        }
    }

    private async Task DownloadAndInstallAsync(AppUpdateOffer offer, CancellationToken cancellationToken)
    {
        if (_service.ApplyNeedsElevation && !await _confirm(
                "Installing needs administrator permission. Windows will ask next.", "Update"))
        {
            return;
        }

        // Downloading interrupts nothing, so it starts at once even while something is running.
        var progress = new PercentProgress(percent => _show(Busy($"Downloading DAQiFi {offer.Version}... {percent}%")));
        _show(Busy($"Downloading DAQiFi {offer.Version}... 0%"));
        try
        {
            await _service.DownloadAsync(offer, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.Warning(ex, $"Downloading app update {offer.Version} failed");
            _show(Offer(offer, "Couldn't download the update.", "Try again"));
            return;
        }

        // Asked again right before applying: anything may have started while waiting.
        var waitingFor = WaitingFor();
        while (waitingFor is not null)
        {
            _show(Busy($"Update ready. Installs {waitingFor}."));
            await _delay(RecheckInterval, cancellationToken);
            waitingFor = WaitingFor();
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Known gap, accepted for now (#509): on an install for all users, the updater's administrator
        // prompt comes after this last check. A copy another user starts while that prompt is open
        // is not seen here, and the updater closes it once the prompt is approved.
        try
        {
            _service.ApplyAndRestart(offer);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Applying app update {offer.Version} failed");
            _show(Offer(offer, "Couldn't install the update.", "Try again"));
        }
    }

    private Notifications Offer(AppUpdateOffer offer, string message, string actionText) => new()
    {
        Message = message,
        ActionText = actionText,
        Action = new AsyncRelayCommand(() => InstallAsync(offer)),
    };

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
    private string? WaitingFor()
    {
        try
        {
            var own = _blocker() switch
            {
                UpdateBlocker.Logging => "when logging stops",
                UpdateBlocker.FirmwareUpdate => "when the firmware update finishes",
                UpdateBlocker.OtherWindowOpen => "when DAQiFi's other windows are closed",
                UpdateBlocker.SdCardTransfer => "when the SD card transfer finishes",
                UpdateBlocker.LoggedDataBusy => "when the import or delete finishes",
                _ => null,
            };
            if (own is not null) { return own; }

            return _service.IsInstallInUseElsewhere() ? OTHER_COPIES : null;
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
