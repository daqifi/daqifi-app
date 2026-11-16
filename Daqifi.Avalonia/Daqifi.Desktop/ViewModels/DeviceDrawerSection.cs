// Downstream-only: no upstream counterpart. Issues #569 and #644.

namespace Daqifi.Desktop.ViewModels;

/// <summary>
/// A section of the device drawer that a notification button can open the drawer at.
/// </summary>
public enum DeviceDrawerSection
{
    /// <summary>The FIRMWARE section: the update control (issue #569).</summary>
    Firmware,

    /// <summary>The NETWORK section: the WiFi settings (issue #644).</summary>
    Network,
}
