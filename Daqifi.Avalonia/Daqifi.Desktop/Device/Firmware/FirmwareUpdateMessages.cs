// Downstream-only: no upstream counterpart. Issue #570.

using Daqifi.Core.Device.Network;
using Daqifi.Core.Firmware;

namespace Daqifi.Desktop.Device.Firmware;

/// <summary>
/// The words the user reads during and after a firmware update. Kept apart from
/// <see cref="FirmwareUpdateCoordinator"/> so the text is one place and testable on its own.
/// </summary>
/// <remarks>
/// Before issue #570 the status line copied <see cref="FirmwareUpdateProgress.CurrentOperation"/>
/// from Daqifi.Core, which is Core's own engineering text ("Programming record 4587 of 54957",
/// "Jumping to application firmware."). A user does not know what a record is, and the progress
/// bar already shows how far the write has gone. The report also carries
/// <see cref="FirmwareUpdateProgress.State"/>, which this class turns into user words instead.
/// </remarks>
public static class FirmwareUpdateMessages
{
    /// <summary>
    /// The text for a state that this class does not know — a value a later Core release adds.
    /// The update still shows a sensible line rather than a blank or a Core string.
    /// </summary>
    internal const string UnknownStateText = "Updating firmware...";

    /// <summary>
    /// The status line for one <see cref="FirmwareUpdateState"/>. Every value gives a text, and no
    /// text contains a record count: the progress bar shows the amount.
    /// </summary>
    public static string DescribeState(FirmwareUpdateState state) => state switch
    {
        FirmwareUpdateState.Idle => "Preparing firmware update...",
        FirmwareUpdateState.PreparingDevice => "Preparing the device...",
        FirmwareUpdateState.WaitingForBootloader => "Restarting the device in update mode...",
        FirmwareUpdateState.Connecting => "Connecting to the device...",
        FirmwareUpdateState.ErasingFlash => "Erasing the old firmware...",
        FirmwareUpdateState.Programming => "Writing firmware...",
        FirmwareUpdateState.Verifying => "Making sure that the firmware is correct...",
        FirmwareUpdateState.JumpingToApp => "Restarting the device...",
        FirmwareUpdateState.ReconnectingAfterFlash => "Connecting to the device again...",
        FirmwareUpdateState.Complete => "Firmware update complete.",
        FirmwareUpdateState.CleaningUp => "The update did not complete. Making the device safe...",
        FirmwareUpdateState.Recovered => "The update did not complete. The device is safe to update again.",
        FirmwareUpdateState.Failed => "The update did not complete.",
        _ => UnknownStateText,
    };

    /// <summary>
    /// The text of the success dialog: it names the device, the version the device reports after
    /// its restart, and the version it had before.
    /// </summary>
    /// <param name="deviceName">The name the user knows the device by.</param>
    /// <param name="oldVersion">The firmware the device had before the update; empty if unknown.</param>
    /// <param name="reportedVersion">
    /// The version the device reported after its restart. Empty when the device did not report
    /// one: the text then says that the new version is unknown. It never names a release that the
    /// app looked up, because that release can differ from the one that was written.
    /// </param>
    public static string UpdateSucceeded(string deviceName, string? oldVersion, string? reportedVersion)
    {
        var device = string.IsNullOrWhiteSpace(deviceName) ? "The device" : deviceName.Trim();
        var old = oldVersion?.Trim() ?? string.Empty;
        var reported = reportedVersion?.Trim() ?? string.Empty;
        var oldSentence = old.Length > 0 ? $" The old version was {old}." : string.Empty;

        if (reported.Length == 0)
        {
            return $"{device} has new firmware. The new version is unknown, because the device did not report it.{oldSentence}";
        }

        if (string.Equals(old, reported, StringComparison.OrdinalIgnoreCase))
        {
            return $"{device} now has firmware {reported}. This is the same version as before.";
        }

        return $"{device} now has firmware {reported}.{oldSentence}";
    }

    /// <summary>
    /// The name the user knows the device by: the friendly name when set, otherwise the part number
    /// the device tile shows ("Nq1"), otherwise the serial, otherwise the connection name.
    /// </summary>
    /// <remarks>
    /// The one name rule for every firmware message: the confirmation dialog, the success dialog and
    /// the notifications (issue #644). Before #644 the dialogs used this rule and the notification
    /// after an update used <c>DeviceDisplayName</c>, which skips the part number, so one event named
    /// one device "Nq1" in the dialog and "7E2815916200E898" in the notification.
    /// </remarks>
    public static string DeviceNameFor(IStreamingDevice device)
    {
        if (!string.IsNullOrWhiteSpace(device.FriendlyName))
        {
            return device.FriendlyName;
        }

        if (!string.IsNullOrWhiteSpace(device.DevicePartNumber))
        {
            return device.DevicePartNumber;
        }

        return !string.IsNullOrWhiteSpace(device.DeviceSerialNo)
            ? DeviceSerials.ToDisplay(device.DeviceSerialNo)
            : device.Name;
    }

    /// <summary>
    /// What the user is told about the WiFi settings after an update when the app does not know
    /// whether they changed: the device did not report its settings before or after the update.
    /// </summary>
    /// <param name="device">The name of the device, from <see cref="DeviceNameFor"/>.</param>
    public static string WifiSettingsMayBeErased(string device) =>
        $"The firmware update can erase the WiFi settings and the calibration of {device}. " +
        "Set the WiFi settings again.";

    /// <summary>
    /// The label of the notification button that opens the NETWORK section of the device drawer
    /// (issue #644).
    /// </summary>
    public const string OpenWifiSettingsActionText = "WiFi settings";

    /// <summary>
    /// What the user is told about the WiFi settings after a successful update, from the settings
    /// the device reported before the update and after it (issue #644). Null when the settings did
    /// not change: there is then nothing to tell.
    /// </summary>
    /// <param name="deviceName">
    /// The name from <see cref="DeviceNameFor"/>. Every text names the device, so the notification
    /// can show the text as it is.
    /// </param>
    /// <param name="before">
    /// The settings the device reported before the update, or null when they are not known. Only the
    /// mode and the SSID are compared.
    /// </param>
    /// <param name="after">
    /// The settings the device reported after its restart (issue #642), or null when it did not
    /// report them.
    /// </param>
    /// <remarks>
    /// When the mode or the SSID is not known on one side, the text is
    /// <see cref="WifiSettingsMayBeErased"/>: the app cannot say that nothing changed. When they did
    /// not change, there is no WiFi text and no calibration text. The v3.9.0 release notes say that
    /// an update with an older bootloader erases the WiFi settings and the calibration together, so
    /// WiFi settings that are still there show that this erase did not occur.
    /// </remarks>
    public static string? WifiSettingsAfterUpdate(
        string deviceName,
        ReportedNetworkSettings? before,
        ReportedNetworkSettings? after)
    {
        var named = !string.IsNullOrWhiteSpace(deviceName);
        var device = named ? deviceName.Trim() : "the device";
        var subject = named ? device : "The device";

        if (before?.Mode is not { } oldMode || before.Ssid is not { } oldSsid
            || after?.Mode is not { } newMode || after.Ssid is not { } newSsid)
        {
            return WifiSettingsMayBeErased(device);
        }

        if (oldMode == newMode && string.Equals(oldSsid, newSsid, StringComparison.Ordinal))
        {
            return null;
        }

        const string calibration = "The update can also have erased the calibration of the device.";

        if (newMode == WifiMode.SelfHosted)
        {
            return $"The WiFi settings were erased. {subject} is now its own access point, {newSsid}. " +
                   "To connect it to your network again, open the WiFi settings of the device. " +
                   calibration;
        }

        return $"The WiFi settings changed during the update. {subject} now uses the network {newSsid}. " +
               "Open the WiFi settings of the device and make sure that they are correct. " +
               calibration;
    }
}
