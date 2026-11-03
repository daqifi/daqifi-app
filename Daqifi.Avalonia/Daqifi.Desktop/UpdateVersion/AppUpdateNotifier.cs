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
/// Nothing is downloaded until the user clicks Install (#341: notify only, download on click).
/// Installing restarts the app, so when something is running that the restart would cut off, the
/// update waits for it and installs on its own once it is done: the user clicks once.
/// </remarks>
public sealed class AppUpdateNotifier
{
    /// <summary>How often a waiting update looks again at what it is waiting for.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(10);

    private const string TITLE = "Update";

    private readonly IAppUpdateService _service;
    private readonly Func<UpdateBlocker> _blocker;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Func<string, string, Task> _inform;
    private readonly IAppLogger _logger;
    private readonly Func<TimeSpan, Task> _delay;
    private bool _installing;
    private bool _usageCheckFailureLogged;

    /// <param name="service">The update service for this head.</param>
    /// <param name="blocker">What, if anything, installing would interrupt right now.</param>
    /// <param name="confirm">Asks a yes/no question (message, title); true means go ahead.</param>
    /// <param name="inform">Shows a message (message, title).</param>
    /// <param name="logger">Where failures are recorded.</param>
    /// <param name="delay">Waits between checks while the update waits; <see cref="Task.Delay(TimeSpan)"/> unless a test supplies one.</param>
    public AppUpdateNotifier(
        IAppUpdateService service,
        Func<UpdateBlocker> blocker,
        Func<string, string, Task<bool>> confirm,
        Func<string, string, Task> inform,
        IAppLogger logger,
        Func<TimeSpan, Task>? delay = null)
    {
        _service = service;
        _blocker = blocker;
        _confirm = confirm;
        _inform = inform;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Checks for a newer version. Returns the notification to show, or <c>null</c> when updates are
    /// unsupported, this copy is current, or the check failed. Never throws.
    /// </summary>
    public async Task<Notifications?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_service.Support != AppUpdateSupport.Supported) { return null; }

        AppUpdateOffer? offer;
        try
        {
            offer = await _service.CheckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Offline is normal for this app (field and lab networks), so this is not an error.
            _logger.Warning(ex, "Checking for an app update failed");
            return null;
        }

        if (offer is null) { return null; }

        return new Notifications
        {
            Message = $"DAQiFi {offer.Version} is available.",
            ActionText = "Install and restart",
            Action = new AsyncRelayCommand(() => InstallAsync(offer)),
        };
    }

    /// <summary>
    /// Downloads <paramref name="offer"/>, waits until nothing would be cut off, then installs it and
    /// restarts. A second click while one is under way does nothing.
    /// </summary>
    public async Task InstallAsync(AppUpdateOffer offer)
    {
        if (_installing) { return; }
        _installing = true;
        try
        {
            await DownloadAndInstallAsync(offer);
        }
        finally
        {
            _installing = false;
        }
    }

    private async Task DownloadAndInstallAsync(AppUpdateOffer offer)
    {
        if (_service.ApplyNeedsElevation && !await _confirm(
                "Installing needs administrator permission. Windows will ask next.", TITLE))
        {
            return;
        }

        // Downloading interrupts nothing, so it starts at once even while something is running.
        try
        {
            await _service.DownloadAsync(offer);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, $"Downloading app update {offer.Version} failed");
            await _inform("The update couldn't be downloaded. Try again later.", TITLE);
            return;
        }

        // Asked again right before applying: anything may have started while waiting.
        var waitingFor = WaitingFor();
        if (waitingFor is not null)
        {
            await _inform($"Update ready. DAQiFi will restart to install it {waitingFor}.", TITLE);
            while (waitingFor is not null)
            {
                await _delay(RecheckInterval);
                waitingFor = WaitingFor();
            }
        }

        try
        {
            _service.ApplyAndRestart(offer);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Applying app update {offer.Version} failed");
            await _inform("The update couldn't be installed. Try again later.", TITLE);
        }
    }

    /// <summary>
    /// What the restart would cut off, as the end of "install it ...", or <c>null</c> when nothing.
    /// This process's own work comes first; then other copies, which the updater also closes, in
    /// every user's session. If that check fails the update keeps waiting: closing someone else's
    /// recording is worse than installing later.
    /// </summary>
    private string? WaitingFor()
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

        try
        {
            return _service.IsInstallInUseElsewhere() ? OTHER_COPIES : null;
        }
        catch (Exception ex)
        {
            // Once per install: a waiting update asks again every few seconds.
            if (!_usageCheckFailureLogged)
            {
                _usageCheckFailureLogged = true;
                _logger.Warning(ex, "Checking for other running copies of DAQiFi failed");
            }

            return OTHER_COPIES;
        }
    }

    private const string OTHER_COPIES = "when other copies of DAQiFi on this PC are closed";
}
