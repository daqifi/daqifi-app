using Daqifi.Desktop.Device.Firmware;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins the options the WiFi-module flash runs with, from
/// <see cref="FirmwareUpdateCoordinator.CreateWifiOptions"/>.
/// </summary>
public class WifiFlashOptionsTests
{
    /// <summary>
    /// Core 1.10.0 enters the USB-to-WINC bridge in its prep step (daqifi-core#877). With the
    /// bridge up, firmware v3.9.0 is in USB transparent mode and forwards every byte to the WINC
    /// without parsing it. A callback that writes SCPI at the "Power cycle WINC" prompt therefore
    /// puts ASCII onto the flash tool's programming link (review of PR #582). Core's documentation
    /// for the option says to leave it unset.
    /// </summary>
    [Fact]
    public void The_app_sends_nothing_at_the_WINC_prompt()
    {
        var options = FirmwareUpdateCoordinator.CreateWifiOptions("19.7.7", "COM7");

        Assert.Null(options.WifiBridgeActivationCallback);
    }

    /// <summary>
    /// Core now waits for the bridge to come up before it disconnects. A zero delay would hand the
    /// port to the flash tool before the firmware's queued REINIT has run.
    /// </summary>
    [Fact]
    public void Core_waits_for_the_bridge_before_it_hands_over_the_port()
    {
        var options = FirmwareUpdateCoordinator.CreateWifiOptions("19.7.7", "COM7");

        Assert.True(options.PostLanBridgeEntryDelay > TimeSpan.Zero);
        Assert.Equal("COM7", options.WifiPortOverride);
        Assert.Contains("/v 19.7.7", options.WifiFlashToolArgumentsTemplate);
    }
}
