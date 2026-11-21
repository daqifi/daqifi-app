// Ported from upstream Daqifi.Desktop/Device/Firmware/FirmwareFailureClassifier.cs
// (post-5ecf5ffd shape). The `// @port:` markers name the upstream symbols, matching
// the convention every other file in this folder carries.

using Daqifi.Core.Firmware;
using Daqifi.Desktop.Common.Loggers;

namespace Daqifi.Desktop.Device.Firmware;

/// <summary>
/// Decides whether a Core <see cref="FirmwareUpdateException"/> describes a genuine flash failure or
/// a <em>post-flash reconnect timeout</em> — the firmware was fully written and verified and only the
/// device's return to normal serial operation timed out.
/// <para>
/// A post-flash reconnect timeout is a device/environmental condition a power-cycle finishes, not an
/// app defect: it is logged at Warning (no Sentry capture) and reported to the user as installed.
/// Upstream issue #738 / PR #751 established that treatment for the PIC32 case; issue #776 extends it
/// to the symmetric WiFi-module case.
/// </para>
/// </summary>
// @port: Daqifi.Desktop.Device.Firmware.FirmwareFailureClassifier
public static class FirmwareFailureClassifier
{
    #region Constants
    /// <summary>
    /// Shown when the PIC32 flash finished (erase + program + CRC-verify all passed) but the device
    /// did not re-enumerate its serial port in time (upstream issue #738).
    /// </summary>
    internal const string PIC32_INSTALLED_NO_RECONNECT_MESSAGE =
        "Firmware was installed successfully, but the device did not return to normal mode on its " +
        "own. Please power-cycle the device (unplug and replug its USB cable), then reconnect.";

    /// <summary>
    /// Shown when the WINC flash finished (the flash tool reported its success marker) but the device
    /// did not re-enumerate its serial port in time (upstream issue #776).
    /// </summary>
    internal const string WIFI_INSTALLED_NO_RECONNECT_MESSAGE =
        "WiFi firmware was installed successfully, but the device did not return to normal mode on " +
        "its own. Please power-cycle the device (unplug and replug its USB cable), then reconnect.";
    #endregion

    #region Public Methods
    /// <summary>
    /// Returns <c>true</c> when <paramref name="exception"/> is a post-flash reconnect timeout: the
    /// firmware image was completely written and verified, and the failure is only that the device
    /// did not come back on its serial port in time.
    /// </summary>
    /// <param name="exception">The exception Core threw. Never null.</param>
    /// <returns>
    /// <c>true</c> if the flash itself succeeded and only the reconnect timed out; <c>false</c> for
    /// every genuine flash failure (which must keep the Error/Sentry path).
    /// </returns>
    /// <remarks>
    /// <para>
    /// The discriminator is <em>structural</em>, deliberately not a match on
    /// <see cref="FirmwareUpdateException.Operation"/>. That property carries whatever text Core
    /// passed to its last <c>TransitionToState</c> call — free-form prose with no published
    /// constant, so matching it would silently stop working the first time Core reworded a log line.
    /// The failed state alone is exact, because the two post-flash reconnect states are disjoint
    /// across the two flows:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>PIC32</b> — <see cref="FirmwareUpdateState.JumpingToApp"/> is the LAST step and is entered
    /// only after erase + program + CRC-verify all succeeded, so the firmware is already installed.
    /// <see cref="FirmwareUpdateState.Verifying"/> on this image is the CRC check, a real failure
    /// ("the device's flash CRC did not match"), and is intentionally NOT downgraded.
    /// </description></item>
    /// <item><description>
    /// <b>WiFi module</b> — <see cref="FirmwareUpdateState.ReconnectingAfterFlash"/> is entered at
    /// exactly one place, and only after Core has confirmed the WINC flash tool printed its success
    /// marker; the state covers the serial re-enumeration + LAN restore alone. The WiFi flow never
    /// enters <see cref="FirmwareUpdateState.JumpingToApp"/> (there is no bootloader jump), and the
    /// PIC32 flow never enters <c>ReconnectingAfterFlash</c>.
    /// </description></item>
    /// </list>
    /// <para>
    /// Verified against Daqifi.Core 1.7.0, this app's pinned version (upstream verified the same
    /// shape against 1.4.0, the version it consumes). Core's own documentation for
    /// <c>ReconnectingAfterFlash</c> states the severity this class depends on: "A failure in this
    /// state is benign and environmental: the firmware flashed and verified successfully, and only
    /// the host's re-enumeration of the serial port timed out", and that it is "deliberately
    /// distinct from Verifying, which is a genuine flash failure". <c>WifiModuleUpdater</c>
    /// transitions to <c>ReconnectingAfterFlash</c> immediately after the success-marker check,
    /// wrapping the serial reconnect plus the LAN restore; the state appears nowhere else in Core.
    /// </para>
    /// <para>
    /// This is the post-<c>5ecf5ffd</c> upstream shape. The original (<c>f0e95c3f</c>) keyed the WiFi
    /// half off a caller-supplied <c>FirmwareFlashPhase</c> enum, which existed only because before
    /// Core v1.4.0 the WiFi reconnect shared <see cref="FirmwareUpdateState.Verifying"/> with the
    /// PIC32 CRC check, so only the caller — which knew which image it was flashing — could tell an
    /// installed-but-unreachable device from a bad flash (daqifi-core#398 gap 4). On 1.7.0 that
    /// bandaid is unnecessary and is deliberately not ported.
    /// </para>
    /// </remarks>
    // @port: Daqifi.Desktop.Device.Firmware.FirmwareFailureClassifier.IsPostFlashReconnectTimeout
    public static bool IsPostFlashReconnectTimeout(FirmwareUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.FailedState is FirmwareUpdateState.JumpingToApp
            or FirmwareUpdateState.ReconnectingAfterFlash;
    }

    /// <summary>
    /// The user-facing message for a post-flash reconnect timeout: the firmware installed, and the
    /// device needs a power-cycle to finish the job.
    /// </summary>
    /// <param name="failedState">
    /// The state Core failed in. <see cref="FirmwareUpdateState.ReconnectingAfterFlash"/> is the WiFi
    /// module's post-flash reconnect; anything else reaching here is the PIC32's
    /// <see cref="FirmwareUpdateState.JumpingToApp"/>.
    /// </param>
    /// <returns>The message to present, phrased for the image that was flashed.</returns>
    // @port: Daqifi.Desktop.Device.Firmware.FirmwareFailureClassifier.BuildInstalledButNotReconnectedMessage
    public static string BuildInstalledButNotReconnectedMessage(FirmwareUpdateState failedState)
    {
        return failedState == FirmwareUpdateState.ReconnectingAfterFlash
            ? WIFI_INSTALLED_NO_RECONNECT_MESSAGE
            : PIC32_INSTALLED_NO_RECONNECT_MESSAGE;
    }

    /// <summary>
    /// Logs a firmware failure that Core reported, and returns what to tell the user. This is the one
    /// mapping for both windows that flash the PIC32: the device drawer's update
    /// (<see cref="FirmwareUpdateCoordinator"/>) and the bootloader dialog
    /// (<c>FirmwareDialogViewModel</c>). Both call it, so their text and their log levels cannot
    /// drift apart (issue #672: the bootloader dialog showed one fixed sentence for every failure).
    /// <list type="bullet">
    /// <item><description>
    /// A post-flash reconnect timeout (<see cref="IsPostFlashReconnectTimeout"/>): the firmware is
    /// installed and verified, and only the return to normal mode timed out. It is logged at Warning,
    /// which sends no Sentry event, and the text is
    /// <see cref="BuildInstalledButNotReconnectedMessage"/>: the firmware is installed, power-cycle.
    /// </description></item>
    /// <item><description>
    /// Every other failure is logged at Error. The text is a summary, then Core's
    /// <see cref="FirmwareUpdateException.RecoveryGuidance"/>. Core owns that guidance: since Core
    /// 1.10.0 it says what the failed flash left on the board (<see cref="Pic32FlashCondition"/>: not
    /// changed, a clean bootloader that is safe to re-flash, or possibly half-flashed), so the app
    /// must not write its own.
    /// </description></item>
    /// </list>
    /// <para>
    /// <see cref="FirmwareUpdateState.Verifying"/> is deliberately NOT treated as installed: since
    /// Core v1.4.0 it means only the PIC32 flash CRC check, a genuine failure that keeps its
    /// Error/Sentry path and its "CRC did not match" recovery guidance.
    /// </para>
    /// </summary>
    /// <param name="exception">The exception Core threw. Never null.</param>
    /// <param name="logger">Where the failure is logged.</param>
    /// <returns>Whether the firmware is installed, and the text to show the user.</returns>
    public static FirmwareFailureReport Report(FirmwareUpdateException exception, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(logger);

        // A post-flash reconnect timeout is a device/environmental condition a power-cycle finishes,
        // not an app bug, so it files no Sentry issue for a device that simply needs a power-cycle.
        // Mirrors the serial/WiFi connect classification (upstream issues #589 / #740).
        if (IsPostFlashReconnectTimeout(exception))
        {
            logger.AddBreadcrumb(
                "firmware",
                $"Firmware installed but device did not reconnect: {exception.FailedState}",
                Common.Loggers.BreadcrumbLevel.Warning);
            logger.Warning(
                exception,
                $"Firmware was installed and verified ({exception.FailedState}), but the device did not " +
                "automatically return to application mode after the flash. This is a " +
                "device/environmental condition (power-cycle recovers it), not an app failure.");

            return new FirmwareFailureReport(
                FirmwareInstalled: true,
                BuildInstalledButNotReconnectedMessage(exception.FailedState));
        }

        logger.AddBreadcrumb(
            "firmware",
            $"Firmware update failed: {exception.FailedState}",
            Common.Loggers.BreadcrumbLevel.Error);

        var summary = $"Firmware update failed during '{exception.Operation}' ({exception.FailedState}).";
        logger.Error(exception, summary);

        if (string.IsNullOrWhiteSpace(exception.RecoveryGuidance))
        {
            return new FirmwareFailureReport(FirmwareInstalled: false, summary);
        }

        logger.Warning($"Firmware recovery guidance: {exception.RecoveryGuidance}");
        return new FirmwareFailureReport(
            FirmwareInstalled: false,
            $"{summary}{Environment.NewLine}{Environment.NewLine}Suggested recovery: {exception.RecoveryGuidance}");
    }
    #endregion
}

/// <summary>
/// What a failed firmware operation means for the user. <see cref="FirmwareFailureClassifier.Report"/>
/// makes it.
/// </summary>
/// <param name="FirmwareInstalled">
/// True when the firmware is on the device and only its return to normal mode timed out, so the
/// window reports the run as installed rather than failed.
/// </param>
/// <param name="Message">The text to show the user.</param>
public sealed record FirmwareFailureReport(bool FirmwareInstalled, string Message);
