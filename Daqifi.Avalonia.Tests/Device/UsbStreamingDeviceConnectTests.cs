using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Desktop.Channel;
using Xunit;
using UsbStreamingDevice = Daqifi.Desktop.Device.UsbDevice.UsbStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Runs <see cref="UsbStreamingDevice.Connect"/> end to end, over a real Core streaming device and
/// Core's real <c>InitializeAsync</c>, with a scripted board on the other end of the transport.
/// </summary>
/// <remarks>
/// <para>
/// Issue #669 deleted the wrapper's own "wait for the first status" loop. It was a copy of the wait
/// inside Core's <c>InitializeAsync</c>, and it could not wait: the app subscribes to
/// <c>ChannelsPopulated</c> before it calls <c>InitializeAsync</c>, Core raises that event with a
/// plain <c>Invoke</c>, so the app's handler runs before the handler that ends Core's own wait.
/// These rows pin the two things the deletion relies on, and they pass on the code before the
/// deletion too, because the claim is that nothing changes.
/// </para>
/// <para>
/// The serial wrapper runs the same <c>Connect</c> template, but it builds its own
/// <c>SerialStreamTransport</c> from a port name, so a test cannot give it this transport.
/// </para>
/// </remarks>
public class UsbStreamingDeviceConnectTests
{
    private static readonly string StatusQuery = ScpiMessageProducer.GetDeviceInfo.Data.Trim();

    [Fact]
    public void Connect_returns_with_the_first_status_already_applied_and_asks_for_it_once()
    {
        var transport = ScriptedReplyTransport.Board(BoardStatus());
        var device = new UsbStreamingDevice(transport, "DAQiFi Nq1");
        try
        {
            Assert.True(device.Connect(), device.LastConnectError?.ToString());

            // The wrapper's channels are built only by its ChannelsPopulated handler. They are all
            // here when Connect returns, so that handler ran before Core's wait ended — the wait
            // the deleted loop repeated had nothing left to wait for.
            Assert.Equal(16, device.DataChannels.OfType<AnalogChannel>().Count());
            Assert.Equal("Nq1", device.DevicePartNumber);

            // Core asked once and was answered. Nothing after InitializeAsync asked again.
            Assert.Equal(1, transport.Commands.Count(command => command == StatusQuery));
        }
        finally
        {
            device.Disconnect();
        }
    }

    [Fact]
    public void A_board_that_never_reports_status_fails_the_connect_with_Cores_own_timeout()
    {
        // A board that answers the setup commands and never answers the status request. Core waits
        // its 8 s, asks *IDN? to say why, and throws. The wrapper adds no wait of its own, so the
        // reason the connect keeps is Core's.
        var transport = ScriptedReplyTransport.Board(status: null);
        var device = new UsbStreamingDevice(transport, "DAQiFi Nq1");

        Assert.False(device.Connect());

        var timeout = Assert.IsType<ChannelPopulationTimeoutException>(device.LastConnectError);
        Assert.False(timeout.IsDeviceResponding);
        Assert.True(transport.Commands.Count(command => command == StatusQuery) > 1,
            "Core re-sends the status request while it waits.");
    }

    /// <summary>
    /// The bench Nq1's status reduced to what Core's wait reads. The firmware version is below
    /// v3.5.0, so Core does not ask for a capability document this board would not answer.
    /// </summary>
    private static DaqifiOutMessage BoardStatus() => new()
    {
        DevicePn = "Nq1",
        DeviceFwRev = "3.4.0",
        AnalogInPortNum = 16,
        AnalogInRes = 4095,
        DigitalPortNum = 16
    };
}
