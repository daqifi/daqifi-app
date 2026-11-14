// Downstream-only: no upstream counterpart. Issue #570.

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
    /// the device tile shows ("Nq1"), otherwise the connection name.
    /// </summary>
    public static string DeviceNameFor(IStreamingDevice device)
    {
        if (!string.IsNullOrWhiteSpace(device.FriendlyName))
        {
            return device.FriendlyName;
        }

        return !string.IsNullOrWhiteSpace(device.DevicePartNumber) ? device.DevicePartNumber : device.Name;
    }
}
