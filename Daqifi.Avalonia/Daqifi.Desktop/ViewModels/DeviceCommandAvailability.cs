using Daqifi.Desktop.Device;

namespace Daqifi.Desktop.ViewModels;

/// <summary>
/// The one condition for the device drawer's controls that talk to the device (issue #643): a
/// device is selected, and no firmware update is in progress.
/// </summary>
/// <remarks>
/// During an update the device is in its bootloader and the update owns its connection, so
/// REBOOT, DISCONNECT, APPLY NETWORK SETTINGS, SAVE NAME, the logging mode, the frequency and the
/// firmware file all use this condition. CANCEL UPDATE does not: it is the control that stops an
/// update. The drawer keeps a device selected only while that device is in
/// <c>ConnectionManager.ConnectedDevices</c> (the drawer closes when it leaves), so "selected" is
/// also "connected" here.
/// </remarks>
// Downstream-only: no upstream counterpart.
internal static class DeviceCommandAvailability
{
    /// <summary>The tooltip of a drawer control that this condition disables.</summary>
    public const string UnavailableDuringUpdate = "Not available during a firmware update.";

    /// <summary>True when the drawer may send a command to <paramref name="device"/>.</summary>
    public static bool IsAvailable(IStreamingDevice? device) =>
        device != null && !ConnectionManager.Instance.IsFirmwareUpdateInProgress;

    /// <summary>
    /// Why the drawer's device controls are disabled during a firmware update, and <c>null</c> (no
    /// tooltip) at other times.
    /// </summary>
    public static string? ToolTip =>
        ConnectionManager.Instance.IsFirmwareUpdateInProgress ? UnavailableDuringUpdate : null;
}
