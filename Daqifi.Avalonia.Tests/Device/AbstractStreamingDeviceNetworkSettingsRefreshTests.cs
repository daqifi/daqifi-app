using System.Net;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Network;
using Daqifi.Desktop.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins how a metadata read reaches the NETWORK section of the device drawer (issue #565).
///
/// <para>
/// Both heads bind that section through <c>SelectedDevice.NetworkConfiguration.Mode</c>,
/// <c>.SecurityType</c>, <c>.Ssid</c> and <c>.Password</c>
/// (<c>DevicesPanePrototype.axaml</c> and <c>DevicesMobileView.axaml</c>).
/// <see cref="NetworkConfiguration"/> is a plain Daqifi.Core class with no change notification, so
/// the only thing a binding can watch is the device's <c>NetworkConfiguration</c> property itself.
/// The hydrate used to write the device's new values into the existing instance and raise
/// nothing, so after a firmware update erased the device's WiFi settings the drawer went on
/// showing the old ones.
/// </para>
///
/// <para>
/// A notification alone is not enough. Avalonia's <c>ExpressionNode.SetSource</c> returns early
/// when the new source is the same reference as the old one, so the <c>.Ssid</c> node of a binding
/// does not read again when its parent notifies with the same instance. Measured with a real
/// Avalonia binding under a pumped dispatcher (see the PR for #565): same instance plus
/// notification left the bound text at the old SSID. The new values must therefore arrive in a
/// new instance, which <see cref="The_new_settings_arrive_in_a_new_instance"/> pins.
/// </para>
///
/// <para>
/// This project stands up no Avalonia dispatcher, so these rows pin the two conditions the
/// binding needs (a new instance, and a notification for the property) rather than drive a
/// control.
/// </para>
/// </summary>
public class AbstractStreamingDeviceNetworkSettingsRefreshTests
{
    private const string SiteSsid = "SiteNetwork";

    /// <summary>The access-point SSID the bench Nyquist 1 reported after the 3.9.0 update.</summary>
    private const string AccessPointSsid = "DAQiFi-95AC";

    [Fact]
    public void A_metadata_read_that_changes_the_network_settings_notifies_NetworkConfiguration()
    {
        var device = DeviceOnSiteNetwork();
        var raised = new List<string?>();
        device.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        device.Sync(CoreDeviceReporting(WifiMode.SelfHosted, WifiSecurityType.None, AccessPointSsid));

        Assert.Contains(nameof(AbstractStreamingDevice.NetworkConfiguration), raised);
        Assert.Equal(AccessPointSsid, device.NetworkConfiguration.Ssid);
        Assert.Equal(WifiMode.SelfHosted, device.NetworkConfiguration.Mode);
        Assert.Equal(WifiSecurityType.None, device.NetworkConfiguration.SecurityType);
    }

    [Fact]
    public void The_new_settings_arrive_in_a_new_instance()
    {
        var device = DeviceOnSiteNetwork();
        var before = device.NetworkConfiguration;

        device.Sync(CoreDeviceReporting(WifiMode.SelfHosted, WifiSecurityType.None, AccessPointSsid));

        Assert.NotSame(before, device.NetworkConfiguration);

        // And the old instance is left as it was, so nothing that still holds it sees a half-
        // written mix of old and new values.
        Assert.Equal(SiteSsid, before.Ssid);
        Assert.Equal(WifiMode.ExistingNetwork, before.Mode);
    }

    [Fact]
    public void The_notification_reports_the_new_instance()
    {
        // A binding reads the property when the event arrives, so the value must already be in
        // place at that moment, not assigned after the event.
        var device = DeviceOnSiteNetwork();
        NetworkConfiguration? seen = null;
        device.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(AbstractStreamingDevice.NetworkConfiguration))
            {
                seen = ((AbstractStreamingDevice)sender!).NetworkConfiguration;
            }
        };

        device.Sync(CoreDeviceReporting(WifiMode.SelfHosted, WifiSecurityType.None, AccessPointSsid));

        Assert.NotNull(seen);
        Assert.Equal(AccessPointSsid, seen!.Ssid);
        Assert.Equal(WifiMode.SelfHosted, seen.Mode);
    }

    /// <summary>
    /// The decision for a password that the user typed but did not apply: KEEP it.
    /// </summary>
    /// <remarks>
    /// The device never reports its WiFi password — <see cref="DeviceMetadata"/> has no field for
    /// it — so a metadata read has no device value to show in its place. Clearing it would only
    /// discard what the user typed. The same applies to the static IP fields, which the metadata
    /// also does not carry. Only the three values the device reports (SSID, security type and
    /// mode) change.
    /// </remarks>
    [Fact]
    public void A_password_typed_but_not_applied_survives_the_read()
    {
        var device = DeviceOnSiteNetwork();
        device.NetworkConfiguration.Password = "typed-not-applied";
        device.NetworkConfiguration.StaticIP = IPAddress.Parse("192.168.1.50");
        device.NetworkConfiguration.SubnetMask = IPAddress.Parse("255.255.255.0");
        device.NetworkConfiguration.Gateway = IPAddress.Parse("192.168.1.1");

        device.Sync(CoreDeviceReporting(WifiMode.SelfHosted, WifiSecurityType.None, AccessPointSsid));

        Assert.Equal("typed-not-applied", device.NetworkConfiguration.Password);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), device.NetworkConfiguration.StaticIP);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), device.NetworkConfiguration.SubnetMask);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), device.NetworkConfiguration.Gateway);
    }

    /// <summary>A device whose first metadata read put it on the site's WiFi network.</summary>
    private static TestDevice DeviceOnSiteNetwork()
    {
        var device = new TestDevice();
        device.Sync(CoreDeviceReporting(WifiMode.ExistingNetwork, WifiSecurityType.WpaPskPhrase, SiteSsid));

        // Guard the arrangement: every assertion above passes vacuously if the first read did not
        // land the site settings.
        Assert.Equal(SiteSsid, device.NetworkConfiguration.Ssid);
        Assert.Equal(WifiMode.ExistingNetwork, device.NetworkConfiguration.Mode);
        Assert.Equal(WifiSecurityType.WpaPskPhrase, device.NetworkConfiguration.SecurityType);
        return device;
    }

    private static DaqifiStreamingDevice CoreDeviceReporting(
        WifiMode mode,
        WifiSecurityType securityType,
        string ssid)
    {
        var coreDevice = new DaqifiStreamingDevice("core");
        coreDevice.Metadata.WifiInfrastructureMode = (uint)mode;
        coreDevice.Metadata.WifiSecurityMode = (uint)securityType;
        coreDevice.Metadata.Ssid = ssid;
        return coreDevice;
    }

    private sealed class TestDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        /// <summary>
        /// Re-hydrates from a Core device, as <c>ChannelsPopulated</c> and a reconnect do.
        /// <see cref="AbstractStreamingDevice.SyncFromCoreDevice"/> is protected, so the test
        /// reaches it the way the real subclasses do rather than through reflection.
        /// </summary>
        public void Sync(DaqifiDevice coreDevice) => SyncFromCoreDevice(coreDevice);

        protected override void SendMessage(IOutboundMessage<string> message) =>
            throw new NotSupportedException();
    }
}
