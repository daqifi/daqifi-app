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
}

/// <summary>
/// Turns an available update into a notification, and installs it when the user asks. Replaces the
/// startup check that asked the WPF app's repository for a version it could never parse (#345).
/// </summary>
/// <remarks>
/// Nothing is downloaded until the user clicks Install (#341: notify only, download on click).
/// </remarks>
public sealed class AppUpdateNotifier
{
    private readonly IAppUpdateService _service;
    private readonly Func<UpdateBlocker> _blocker;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Func<string, string, Task> _inform;
    private readonly IAppLogger _logger;

    /// <param name="service">The update service for this head.</param>
    /// <param name="blocker">What, if anything, installing would interrupt right now. Installing restarts the app, so it waits.</param>
    /// <param name="confirm">Asks a yes/no question (message, title); true means go ahead.</param>
    /// <param name="inform">Shows a message (message, title).</param>
    /// <param name="logger">Where failures are recorded.</param>
    public AppUpdateNotifier(
        IAppUpdateService service,
        Func<UpdateBlocker> blocker,
        Func<string, string, Task<bool>> confirm,
        Func<string, string, Task> inform,
        IAppLogger logger)
    {
        _service = service;
        _blocker = blocker;
        _confirm = confirm;
        _inform = inform;
        _logger = logger;
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

    /// <summary>Downloads and installs <paramref name="offer"/>, then restarts into it.</summary>
    public async Task InstallAsync(AppUpdateOffer offer)
    {
        if (await RefuseWhileBusyAsync()) { return; }

        if (_service.ApplyNeedsElevation && !await _confirm(
                $"DAQiFi is installed for all users, so installing version {offer.Version} needs " +
                "administrator permission.\n\nWindows will ask next. Its prompt names Velopack, the " +
                "updater DAQiFi uses.",
                "Install update"))
        {
            return;
        }

        try
        {
            await _service.DownloadAsync(offer);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, $"Downloading app update {offer.Version} failed");
            await _inform($"DAQiFi {offer.Version} could not be downloaded.\n\n{ex.Message}", "Update failed");
            return;
        }

        // The download can take a while; a session or a flash may have started in the meantime.
        if (await RefuseWhileBusyAsync()) { return; }

        try
        {
            _service.ApplyAndRestart(offer);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Applying app update {offer.Version} failed");
            await _inform($"DAQiFi {offer.Version} could not be installed.\n\n{ex.Message}", "Update failed");
        }
    }

    private async Task<bool> RefuseWhileBusyAsync()
    {
        var message = _blocker() switch
        {
            UpdateBlocker.Logging => "Stop logging before installing the update. DAQiFi restarts to install it.",
            UpdateBlocker.FirmwareUpdate =>
                "Wait for the firmware update to finish before installing the update. DAQiFi restarts to " +
                "install it, which would interrupt the device mid-update.",
            _ => null,
        };
        if (message is null) { return false; }

        await _inform(message, "Install update");
        return true;
    }
}
