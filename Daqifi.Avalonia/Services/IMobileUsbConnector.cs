using System;
using System.Threading.Tasks;
using Daqifi.Desktop.Device;

namespace Daqifi.Avalonia.Services;

/// <summary>Outcome of a USB connect attempt: the connected device (null on
/// failure) plus a human-readable status message. The device, when non-null, is
/// already connected and registered with ConnectionManager by the connector.</summary>
public sealed record UsbConnectResult(AbstractStreamingDevice? Device, string Message);

/// <summary>
/// Platform hook for connecting to a DAQiFi over a USB (OTG) host link. The
/// Android head registers an implementation at startup; heads with no USB-host
/// access (desktop / iOS / browser) leave <see cref="MobileUsbConnector.Current"/>
/// null, so <see cref="MobileUsbConnector.IsAvailable"/> is false and the USB
/// affordance stays hidden.
///
/// EXPERIMENTAL — the Android CDC transport this drives is unvalidated on hardware
/// (see docs/mobile-usb-feasibility.md). Connecting registers the device with
/// ConnectionManager, which lights up the SD-offload path (the Storage pane); live
/// streaming in the Stream tab additionally needs MobileShellViewModel generalized
/// off the WiFi device type.
/// </summary>
public interface IMobileUsbConnector
{
    /// <summary>
    /// True when a DAQiFi USB device is attached and enumerable right now.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY UNBOUND — nothing in the UI reads this, and that is the design, not an
    /// oversight (#460). The affordance's visibility is gated on
    /// <see cref="MobileUsbConnector.IsAvailable"/>, a platform capability ("this head can do
    /// USB host"), not on cable state: a phone user with the cable unplugged should still see
    /// the button and get <see cref="ConnectAsync"/>'s explicit "attach it via a USB-C OTG
    /// cable" message, rather than hunting for a control that silently is not there — which is
    /// exactly the failure #460 was.
    /// <para>
    /// Binding visibility to this instead would need cable-state change notification (a runtime
    /// <c>UsbManager.ActionUsbDeviceAttached</c>/<c>Detached</c> receiver feeding
    /// <see cref="MobileUsbConnector.AvailabilityChanged"/>) and hardware validation of both
    /// edges. Until that exists, this stays a read-only probe for the connector's own use and
    /// for diagnostics — keep it; an unreferenced member here is intentional.
    /// </para>
    /// </remarks>
    bool IsDeviceAttached { get; }

    /// <summary>
    /// Request permission, connect the USB device, and register it with
    /// ConnectionManager. Returns the connected device (null on failure) plus a
    /// status message for the caller to surface — never throws.
    /// </summary>
    Task<UsbConnectResult> ConnectAsync();
}

/// <summary>
/// Static registration point: the platform head sets <see cref="Current"/> at startup,
/// mirroring <see cref="NetworkDiscoveryScope"/>.
/// </summary>
/// <remarks>
/// <see cref="AvailabilityChanged"/> exists because "at startup" is not early enough to be
/// relied on, and #460 is what that cost. Under Avalonia 12 the Android head builds its whole
/// view tree from <c>Application.OnCreate</c> — measured on a Galaxy A16, the mobile shell's
/// DataContext was assigned 161 ms BEFORE the first activity's <c>OnCreate</c> was even entered
/// — so a registration made from an activity lands after every one-way binding over
/// <see cref="IsAvailable"/> has already read <c>false</c> and latched it. The registration has since moved earlier (MainApplication), but ordering alone is
/// a fragile guarantee: the next framework migration can move the view build again, silently, and
/// nothing would fail. Raising a change notification makes the order stop mattering.
/// <para>
/// Set <see cref="Current"/> from the UI thread. Handlers run synchronously on the setting
/// thread and typically end in a <c>PropertyChanged</c> that Avalonia bindings consume.
/// </para>
/// </remarks>
public static class MobileUsbConnector
{
    private static IMobileUsbConnector? _current;

    public static IMobileUsbConnector? Current
    {
        get => _current;
        set
        {
            if (ReferenceEquals(_current, value)) { return; }
            _current = value;
            AvailabilityChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Raised whenever <see cref="Current"/> changes, so a view-model that exposes
    /// <see cref="IsAvailable"/> can re-raise <c>PropertyChanged</c> and let a bound
    /// <c>IsVisible</c> re-read it.
    /// </summary>
    public static event EventHandler? AvailabilityChanged;

    /// <summary>True on a platform that has a USB-host connector registered.</summary>
    public static bool IsAvailable => Current is not null;

    /// <summary>True when a connector is registered AND a device is attached.</summary>
    /// <remarks>Deliberately unbound — see <see cref="IMobileUsbConnector.IsDeviceAttached"/>.</remarks>
    public static bool IsDeviceAttached => Current?.IsDeviceAttached == true;

    public static Task<UsbConnectResult> ConnectAsync() =>
        Current?.ConnectAsync()
        ?? Task.FromResult(new UsbConnectResult(null, "USB is not available on this platform."));
}
