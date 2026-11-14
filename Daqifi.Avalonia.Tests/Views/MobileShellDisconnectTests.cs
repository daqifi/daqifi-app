using Daqifi.Avalonia.Tests.Device;
using Daqifi.Avalonia.Views;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CoreStreamingDevice = Daqifi.Core.Device.DaqifiStreamingDevice;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #598: the mobile shell had no Disconnect. The Nyquist accepts one TCP client, so a phone
/// that connected kept the board from the desktop app and every other phone until the app process
/// ended. These tests drive the shell's real <c>DisconnectCommand</c> against the app's device
/// wrapper over a real Core device and a transport that accepts writes and never answers.
/// </summary>
/// <remarks>
/// In the <c>ConnectionManager</c> singleton collection because the shell's adopt step registers
/// the device with <c>ConnectionManager.Instance</c>, and Disconnect must unregister it.
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class MobileShellDisconnectTests : IDisposable
{
    private const int AnalogPorts = 16;
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    private readonly CapturingTransport _transport = new();
    private readonly CoreStreamingDevice _core;
    private readonly ShellTestDevice _device;
    private readonly MobileShellViewModel _shell = new();

    public MobileShellDisconnectTests()
    {
        _core = new CoreStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _core.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = AnalogPorts,
            AnalogInRes = 65535
        });

        _device = new ShellTestDevice("DAQiFi-TEST");
        _device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };
        _device.AttachCore(_core);
        _device.SyncFromCore(_core);
        // After the sync, which writes the metadata the Core device reports (none, here).
        _device.IpAddress = "192.168.4.1";
        _device.DeviceVersion = "3.9.0";
    }

    public void Dispose()
    {
        _shell.Dispose();
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();
    }

    [Fact]
    public void Disconnect_can_run_only_while_a_device_is_connected()
    {
        Assert.False(_shell.DisconnectCommand.CanExecute(null));

        _device.AdoptInto(_shell);
        Assert.True(_shell.DisconnectCommand.CanExecute(null));

        _shell.DisconnectCommand.Execute(null);
        Assert.False(_shell.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public void The_device_bar_names_the_connected_device_and_is_cleared_by_disconnect()
    {
        _device.AdoptInto(_shell);

        Assert.Equal("DAQiFi-TEST", _shell.ConnectedDeviceName);
        Assert.Equal("192.168.4.1  •  FW 3.9.0", _shell.ConnectedDeviceDetail);

        _shell.DisconnectCommand.Execute(null);

        Assert.Equal("", _shell.ConnectedDeviceName);
        Assert.Equal("", _shell.ConnectedDeviceDetail);
    }

    [Fact]
    public async Task Disconnect_unregisters_the_device_and_disconnects_it_once()
    {
        _device.AdoptInto(_shell);
        Assert.Contains(_device, ConnectionManager.Instance.ConnectedDevices);

        _shell.DisconnectCommand.Execute(null);

        Assert.False(_shell.IsConnected);
        Assert.True(_shell.ShowDeviceList, "After a disconnect the device list must show again.");
        Assert.False(_shell.ShowChannelSelector);
        Assert.DoesNotContain(_device, ConnectionManager.Instance.ConnectedDevices);
        Assert.Equal("Disconnected from DAQiFi-TEST. Tap Scan to find devices.", _shell.Status);

        await _shell.PendingRelease.WaitAsync(ReleaseTimeout);
        Assert.Equal(1, _device.DisconnectCalls);
    }

    [Fact]
    public async Task Disconnect_while_streaming_stops_the_stream_on_the_device_first()
    {
        _device.AdoptInto(_shell);
        _shell.StreamToggleCommand.Execute(null);
        Assert.True(_shell.IsStreaming, $"Arrangement: streaming did not start ({_shell.Status}).");
        Assert.True(
            _transport.WaitForSentText("SYSTem:StartStreamData", ReleaseTimeout),
            "Arrangement: the device was never told to stream.");
        _transport.ClearSent();

        _shell.DisconnectCommand.Execute(null);

        Assert.False(_shell.IsStreaming);
        Assert.False(_shell.IsConnected);
        // A client that leaves while the board streams leaves the board streaming into a dead
        // socket (daqifi-nyquist-firmware#1072), so the stop must reach the device.
        Assert.True(
            _transport.WaitForSentText("SYSTem:StopStreamData", ReleaseTimeout),
            $"Disconnect did not stop the stream on the device. Sent: {_transport.SentText}");

        await _shell.PendingRelease.WaitAsync(ReleaseTimeout);
        Assert.Equal(1, _device.DisconnectCalls);
    }

    [Fact]
    public void Disconnect_with_nothing_connected_does_nothing()
    {
        var before = _shell.Status;

        _shell.DisconnectCommand.Execute(null);

        Assert.Equal(before, _shell.Status);
        Assert.True(_shell.PendingRelease.IsCompleted);
        Assert.Equal(0, _device.DisconnectCalls);
    }

    [Fact]
    public void A_USB_device_bar_says_USB_instead_of_an_address() =>
        Assert.Equal(
            "USB  •  FW 3.9.0",
            MobileShellViewModel.DescribeConnection(new UsbShellDevice { DeviceVersion = "3.9.0" }));

    [Fact]
    public void A_device_that_reported_nothing_still_reads_as_connected() =>
        Assert.Equal("Connected", MobileShellViewModel.DescribeConnection(new ShellTestDevice("x")));

    private sealed class UsbShellDevice : Daqifi.Desktop.Device.AbstractStreamingDevice
    {
        public override Daqifi.Desktop.Device.ConnectionType ConnectionType =>
            Daqifi.Desktop.Device.ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message)
        {
        }
    }
}
