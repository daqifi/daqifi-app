using Daqifi.Avalonia.Tests.Device;
using Daqifi.Core.Device;
using Daqifi.Desktop;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device.SerialDevice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Issue #441: a USB unplug has one detector, Core's, and it reaches the app's teardown.
/// </summary>
/// <remarks>
/// <para>
/// The app used to run its own USB watcher (WMI on Windows, a serial-port-list poll on macOS and
/// Linux) beside Core's. Core's <c>SerialStreamTransport</c> polls for its own port every second;
/// on two consecutive misses, or five consecutive I/O faults, it closes the port and raises its
/// <c>StatusChanged</c> with <c>IsConnected == false</c>, which the Core device reports as
/// <see cref="ConnectionStatus.Lost"/>. Both detectors ended in the same teardown and raced for it.
/// The app's watcher is deleted, so this path must carry every USB unplug alone.
/// </para>
/// <para>
/// These tests start at the transport's report. Below it is Core's port-presence poll, which Core
/// covers itself (<c>SerialStreamTransportDropDetectionTests</c>), and a real cable pull, which no
/// test can do. Above it everything is production code: a real Core
/// <see cref="DaqifiStreamingDevice"/>, a real <see cref="SerialStreamingDevice"/> wrapper, and
/// <see cref="ConnectionManager"/>'s teardown.
/// </para>
/// </remarks>
public sealed class UsbUnplugDetectionTests : IDisposable
{
    private readonly CapturingTransport _transport = new();
    private readonly DaqifiStreamingDevice _core;
    private readonly ProbeSerialDevice _device;
    private readonly List<IChannel> _subscribed = [];
    private readonly List<IChannel> _released = [];
    private readonly ConnectionManager _manager;

    public UsbUnplugDetectionTests()
    {
        _core = new DaqifiStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();

        _device = new ProbeSerialDevice("/dev/cu.usbmodem-test-441", _core) { DeviceSerialNo = "SN-USB-441" };
        // The one Core event the unplug path needs, subscribed the way Connect subscribes it.
        _core.StatusChanged += _device.ForwardCoreStatus;

        var channel = new FakeChannel("AI0", "SN-USB-441");
        _device.DataChannels.Add(channel);
        _subscribed.Add(channel);

        _manager = new ConnectionManager(
            () => _subscribed.ToList(),
            released =>
            {
                _released.Add(released);
                _subscribed.Remove(released);
            },
            action => action());
        _manager.ConnectedDevices.Add(_device);
        _manager.SubscribeDeviceEvents(_device);
    }

    public void Dispose()
    {
        _core.StatusChanged -= _device.ForwardCoreStatus;
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();
    }

    [Fact]
    public void A_transport_that_reports_its_port_gone_removes_the_USB_device()
    {
        var reported = new List<ConnectionStatus>();
        _core.StatusChanged += (_, e) => reported.Add(e.Status);

        // What SerialStreamTransport does when its port-presence poll misses twice.
        _transport.Disconnect();

        // Lost, and then Disconnected from the teardown it starts. The teardown runs inside the
        // Lost notification, so a handler later in the list can see the two in either order.
        Assert.Contains(ConnectionStatus.Lost, reported);
        Assert.DoesNotContain(_device, _manager.ConnectedDevices);
        Assert.Single(_released);
        Assert.Empty(_subscribed);
    }

    [Fact]
    public void The_user_is_told_which_USB_device_went_away()
    {
        _transport.Disconnect();

        Assert.True(_manager.NotifyConnection);
        Assert.Equal("SN-USB-441 disconnected (connection lost).", _manager.LastDisconnectReason);
    }

    [Fact]
    public void A_USB_device_being_firmware_updated_is_left_alone()
    {
        // The board drops off its port when it reboots into the bootloader mid-update, and Core
        // reconnects it itself (#738).
        _manager.DeviceBeingUpdated = _device;

        _transport.Disconnect();

        Assert.Contains(_device, _manager.ConnectedDevices);
        Assert.Empty(_released);
        Assert.False(_manager.NotifyConnection);
    }

    [Fact]
    public void A_user_disconnect_is_not_reported_as_an_unplug()
    {
        _manager.Disconnect(_device);
        _released.Clear();

        Assert.DoesNotContain(_device, _manager.ConnectedDevices);
        Assert.False(_manager.NotifyConnection);
        Assert.Empty(_released);
    }

    /// <summary>
    /// The real USB wrapper, with the handler <c>Connect</c> subscribes to Core's
    /// <c>StatusChanged</c> reachable from a test.
    /// </summary>
    private sealed class ProbeSerialDevice(string portName, DaqifiStreamingDevice core)
        : SerialStreamingDevice(portName, core)
    {
        public void ForwardCoreStatus(object? sender, DeviceStatusEventArgs e) => OnCoreStatusChanged(sender, e);
    }
}
