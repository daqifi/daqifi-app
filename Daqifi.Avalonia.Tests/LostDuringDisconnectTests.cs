using Daqifi.Avalonia.Tests.Device;
using Daqifi.Core.Device;
using Daqifi.Desktop;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device.SerialDevice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Issue #690: Core reports <see cref="ConnectionStatus.Lost"/> while the user's own disconnect
/// runs, and the app must not treat that as an unplug.
/// </summary>
/// <remarks>
/// <para>
/// Core reports a drop whenever its transport finds the port gone, on its own schedule. A cable
/// pulled while the user disconnects can land that report inside the disconnect. Two detaches
/// keep it out of the drop path, one per layer:
/// </para>
/// <list type="number">
/// <item><c>ConnectionManager.Disconnect</c> detaches its <c>ConnectionLost</c> handler before it
/// calls the device's <c>Disconnect</c>, so a report there does not start a second teardown or
/// the "device disconnected" notification.</item>
/// <item><c>AbstractStreamingDevice.Disconnect</c> detaches Core's <c>StatusChanged</c> before
/// the teardown touches the Core device, so a report there does not raise
/// <c>ConnectionLost</c> at all.</item>
/// </list>
/// <para>
/// Core's own <c>Disconnect</c> reports nothing: it sets a flag that stops the report before it
/// closes the transport. So a disconnect alone gives neither detach anything to stop, and the
/// existing tests could not see them. Here the test double drops the transport one time, at the
/// start of one of the two windows, and Core reports <see cref="ConnectionStatus.Lost"/> for
/// real. Everything else is production code: a real Core <see cref="DaqifiStreamingDevice"/>, a
/// real <see cref="SerialStreamingDevice"/>, and <see cref="ConnectionManager"/>.
/// </para>
/// </remarks>
public sealed class LostDuringDisconnectTests : IDisposable
{
    private readonly CapturingTransport _transport = new();
    private readonly DaqifiStreamingDevice _core;
    private readonly List<ConnectionStatus> _reported = [];
    private readonly List<IChannel> _subscribed = [];
    private readonly List<IChannel> _released = [];

    public LostDuringDisconnectTests()
    {
        _core = new DaqifiStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _core.StatusChanged += RecordStatus;
    }

    public void Dispose()
    {
        _core.StatusChanged -= RecordStatus;
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();
    }

    private void RecordStatus(object? sender, DeviceStatusEventArgs e) => _reported.Add(e.Status);

    /// <summary>
    /// The positive twin of the test below, and the proof that the double is wired the way
    /// <c>Connect</c> wires a real device: the same drop, with no disconnect running, raises
    /// <c>ConnectionLost</c>. Without this, the test below would also pass on a double whose
    /// handler was never attached.
    /// </summary>
    [Fact]
    public void A_drop_with_no_disconnect_running_raises_ConnectionLost()
    {
        var device = ADevice(DropPoint.None);
        var reasons = new List<string>();
        device.ConnectionLost += (_, e) => reasons.Add(e.Reason);

        _transport.Disconnect();

        Assert.Equal(["connection lost"], reasons);
    }

    /// <summary>
    /// Gap 1 (M1). The transport drops after <c>Disconnect</c> detached Core's
    /// <c>StatusChanged</c> and before Core's own disconnect starts. Core reports
    /// <see cref="ConnectionStatus.Lost"/>, and the device must not raise <c>ConnectionLost</c>.
    /// </summary>
    [Fact]
    public void A_drop_during_the_teardown_of_a_user_disconnect_does_not_raise_ConnectionLost()
    {
        var device = ADevice(DropPoint.CleanupStart);
        var reasons = new List<string>();
        device.ConnectionLost += (_, e) => reasons.Add(e.Reason);

        Assert.True(device.Disconnect());

        // The premise: Core did report the drop, so the empty list below means something.
        Assert.Contains(ConnectionStatus.Lost, _reported);
        Assert.Empty(reasons);
    }

    /// <summary>
    /// Gap 2 (M2). The transport drops when the device's <c>Disconnect</c> starts, before the
    /// device detaches anything, so the device raises <c>ConnectionLost</c>. The manager detached
    /// its handler first, so the disconnect stays the user's: one teardown, no channels released by
    /// the drop path, and no "device disconnected" notification.
    /// </summary>
    /// <remarks>
    /// The test manager runs its UI marshal inline. In the app the marshal runs inline only for a
    /// report on the UI thread, which is the thread the user's disconnect runs on. A report from
    /// another thread is posted, and when it runs the device has left the list, so the "already
    /// torn down" check stops it too. For a report on the UI thread, this detach is the only guard.
    /// </remarks>
    [Fact]
    public void A_drop_while_the_manager_disconnects_the_device_is_not_reported_as_an_unplug()
    {
        var device = ADevice(DropPoint.DisconnectStart);
        var manager = AManagerHolding(device);

        manager.Disconnect(device);

        // The premise: Core did report the drop inside the disconnect.
        Assert.Contains(ConnectionStatus.Lost, _reported);
        Assert.DoesNotContain(device, manager.ConnectedDevices);
        Assert.Equal(1, device.DisconnectCalls);
        Assert.Empty(_released);
        Assert.False(manager.NotifyConnection);
        Assert.Equal(string.Empty, manager.LastDisconnectReason);
    }

    private DisconnectingSerialDevice ADevice(DropPoint dropAt)
    {
        var device = new DisconnectingSerialDevice(_core, _transport, dropAt) { DeviceSerialNo = "SN-USB-690" };

        var channel = new FakeChannel("AI0", "SN-USB-690");
        device.DataChannels.Add(channel);
        _subscribed.Add(channel);

        return device;
    }

    private ConnectionManager AManagerHolding(DisconnectingSerialDevice device)
    {
        var manager = new ConnectionManager(
            () => _subscribed.ToList(),
            released =>
            {
                _released.Add(released);
                _subscribed.Remove(released);
            },
            action => action());
        manager.ConnectedDevices.Add(device);
        manager.SubscribeDeviceEvents(device);
        return manager;
    }

    /// <summary>Where in the disconnect the test double drops its transport.</summary>
    private enum DropPoint
    {
        /// <summary>Never. The test drops the transport itself.</summary>
        None,

        /// <summary>When <c>Disconnect</c> starts, before the device detaches anything.</summary>
        DisconnectStart,

        /// <summary>
        /// When <c>CleanupConnection</c> starts: after <c>Disconnect</c> detached Core's events,
        /// before the Core device is disconnected.
        /// </summary>
        CleanupStart,
    }

    /// <summary>
    /// The real USB wrapper on a Core device that is already connected. It subscribes Core's
    /// events with <c>SubscribeCoreDeviceEvents</c>, as <c>Connect</c> does, so the detach in
    /// <c>Disconnect</c> removes the same handler. It drops its transport one time, at
    /// <see cref="DropPoint"/>. Only one time: a second drop during a second teardown would recurse.
    /// </summary>
    private sealed class DisconnectingSerialDevice : SerialStreamingDevice
    {
        private readonly CapturingTransport _transport;
        private readonly DropPoint _dropAt;
        private bool _dropped;

        public DisconnectingSerialDevice(DaqifiStreamingDevice core, CapturingTransport transport, DropPoint dropAt)
            : base("/dev/cu.usbmodem-test-690", core)
        {
            _transport = transport;
            _dropAt = dropAt;
            SubscribeCoreDeviceEvents(core);
        }

        /// <summary>How many times <see cref="Disconnect"/> ran.</summary>
        public int DisconnectCalls { get; private set; }

        public override bool Disconnect()
        {
            DisconnectCalls++;
            DropTransportOnceAt(DropPoint.DisconnectStart);
            return base.Disconnect();
        }

        protected override void CleanupConnection()
        {
            DropTransportOnceAt(DropPoint.CleanupStart);
            base.CleanupConnection();
        }

        private void DropTransportOnceAt(DropPoint point)
        {
            if (_dropAt != point || _dropped)
            {
                return;
            }

            _dropped = true;
            _transport.Disconnect();
        }
    }
}
