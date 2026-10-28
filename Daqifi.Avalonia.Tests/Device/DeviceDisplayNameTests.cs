using System.Net;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Device.UsbDevice;
using Daqifi.Desktop.Device.WiFiDevice;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Issue #462: the mobile Storage pane's CONNECTED DEVICE dropdown showed
/// <c>Daqifi.Desktop.Device.UsbDevice.UsbStreamingDevice</c> — a fully-qualified CLR type name
/// in a user-facing control.
///
/// <para>
/// The ComboBox bound no display member, so Avalonia rendered each item with
/// <c>ToString()</c>. <see cref="DaqifiStreamingDevice"/> overrides that to return its name,
/// which is why the WiFi path always looked right and hid this for as long as WiFi was the only
/// transport the mobile pane could reach. <see cref="UsbStreamingDevice"/> declares no override,
/// so it fell through to <see cref="object.ToString"/>.
/// </para>
///
/// <para>
/// The view now binds <see cref="IStreamingDevice.DeviceDisplayName"/> — the ported desktop
/// concept for "best available human-readable name", so the two heads agree — and these tests
/// pin the VALUE that binding reads: which of friendly name, serial number and link identifier
/// wins, for every transport.
/// </para>
///
/// <para>
/// These are contract tests, NOT the regression guard for #462, and the distinction cost me a
/// wrong assumption worth recording. Every case here passes against the broken markup, because
/// the property was always correct and the bug was that nothing read it. The guard that actually
/// fails on the unfixed view is textual —
/// <c>DeviceLogsMobileViewBindingTests.The_device_selector_renders_a_name_and_not_a_type</c> —
/// because the ABSENCE of a declaration was the whole defect. That is the inverse of #460, where
/// the declaration was present and correct and only its value was wrong, and where a text guard
/// was therefore blind. Which kind of test can see a binding bug depends on which half of the
/// binding broke; neither kind is reliably the answer.
/// </para>
/// </summary>
public sealed class DeviceDisplayNameTests
{
    private static UsbStreamingDevice Usb() => new(new CapturingTransport(), "DAQiFi USB");

    private static DaqifiStreamingDevice WiFi() =>
        new(IPAddress.Parse("192.168.1.30"), 9760, "DAQiFi WiFi");

    private static SerialStreamingDevice Serial() =>
        new("/dev/null", "DAQiFi Serial", null, null);

    // AbstractStreamingDevice, not IStreamingDevice: FriendlyName is read-only on the
    // interface (the device sets it from status frames) and settable on the base class.
    public static TheoryData<string, AbstractStreamingDevice> EveryTransport() => new()
    {
        { "usb", Usb() },
        { "wifi", WiFi() },
        { "serial", Serial() },
    };

    /// <summary>
    /// The defect, stated as the rule it broke: whatever a device offers the UI as its name, it
    /// is never the name of its own class. Checked against the real type of each instance rather
    /// than a hardcoded string, so a renamed or newly added device type cannot slip past.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryTransport))]
    public void No_transport_offers_its_CLR_type_as_a_display_name(string transport, AbstractStreamingDevice device)
    {
        var displayName = device.DeviceDisplayName;
        var type = device.GetType();

        Assert.False(string.IsNullOrWhiteSpace(displayName),
            $"the {transport} device offers no display name at all");
        Assert.NotEqual(type.FullName, displayName);
        Assert.NotEqual(type.Name, displayName);
        Assert.DoesNotContain("Daqifi.Desktop.Device", displayName, StringComparison.Ordinal);
    }

    /// <summary>
    /// The name the user set ON THE DEVICE wins over everything else. This is the reason to bind
    /// <c>DeviceDisplayName</c> rather than <c>Name</c>: <c>Name</c> is whatever the connect site
    /// happened to label the device with, and it does not track <c>friendly_device_name</c> as it
    /// arrives on status frames.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryTransport))]
    public void A_user_set_friendly_name_wins(string transport, AbstractStreamingDevice device)
    {
        device.DeviceSerialNo = "9090539562006014104";
        device.FriendlyName = $"Bench {transport}";

        Assert.Equal($"Bench {transport}", device.DeviceDisplayName);
    }

    /// <summary>
    /// With no friendly name, the serial number identifies the device. Ugly, but unambiguous
    /// when two identical units are plugged in — which is the case the dropdown exists for.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryTransport))]
    public void Without_a_friendly_name_the_serial_number_identifies_it(string transport, AbstractStreamingDevice device)
    {
        _ = transport;
        device.DeviceSerialNo = "9090539562006014104";

        Assert.Equal("9090539562006014104", device.DeviceDisplayName);
    }

    /// <summary>
    /// With neither, each transport falls back to the thing that locates it on this machine: the
    /// IP for WiFi, the port for serial, and the device name for USB, which has no COM port on
    /// mobile. All three are identifiers a person can act on; none is a type name.
    /// </summary>
    [Fact]
    public void With_neither_each_transport_falls_back_to_its_link_identifier()
    {
        Assert.Equal("192.168.1.30", WiFi().DeviceDisplayName);
        Assert.Equal("/dev/null", Serial().DeviceDisplayName);
        Assert.Equal("DAQiFi USB", Usb().DeviceDisplayName);
    }
}
