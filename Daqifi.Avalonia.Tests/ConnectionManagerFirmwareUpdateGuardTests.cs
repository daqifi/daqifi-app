using Daqifi.Avalonia.Tests.Device;
using Daqifi.Desktop;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Issue #538, the second barrier: <see cref="ConnectionManager.Disconnect"/> and
/// <see cref="ConnectionManager.Reboot"/> leave the device of a firmware update alone. The firmware
/// update uses that device's connection until it ends. The drop paths already obeyed
/// <c>DeviceBeingUpdated</c> (issue #738); these two user paths did not, so a click on DISCONNECT
/// or REBOOT during the write disposed the connection and the update failed.
/// </summary>
/// <remarks>
/// These construct their own <see cref="ConnectionManager"/>, so they share no state with the
/// singleton and stay outside <c>ConnectionManagerSingletonCollection</c>. The devices are
/// <see cref="DroppableTestDevice"/>s: no port and no socket opens.
/// </remarks>
public class ConnectionManagerFirmwareUpdateGuardTests
{
    private static ConnectionManager NewManager() =>
        new(() => new List<IChannel>(),
            _ => { },
            action => action());

    private static DroppableTestDevice Connected(ConnectionManager manager, string serial)
    {
        var device = new DroppableTestDevice(serial, ConnectionType.Usb) { RecordSentMessages = true };
        manager.ConnectedDevices.Add(device);
        return device;
    }

    [Fact]
    public void Disconnect_leaves_the_device_of_a_firmware_update_connected()
    {
        var manager = NewManager();
        var device = Connected(manager, "SN-538-A");
        manager.DeviceBeingUpdated = device;

        manager.Disconnect(device);

        Assert.Contains(device, manager.ConnectedDevices);
        Assert.Equal(0, device.DisconnectCalls);
    }

    [Fact]
    public void Reboot_sends_nothing_to_the_device_of_a_firmware_update()
    {
        var manager = NewManager();
        var device = Connected(manager, "SN-538-B");
        manager.DeviceBeingUpdated = device;

        manager.Reboot(device);

        Assert.Contains(device, manager.ConnectedDevices);
        Assert.Empty(device.SentMessages);
        Assert.Equal(0, device.DisconnectCalls);
    }

    /// <summary>
    /// The guard in the other direction: without an update, the two commands do what they did
    /// before. This also proves that the two tests above can fail — the double does disconnect and
    /// does send the reboot command when nothing stops it.
    /// </summary>
    [Fact]
    public void Without_an_update_disconnect_and_reboot_remove_the_device()
    {
        var manager = NewManager();
        var disconnected = Connected(manager, "SN-538-C");
        var rebooted = Connected(manager, "SN-538-D");

        manager.Disconnect(disconnected);
        manager.Reboot(rebooted);

        Assert.DoesNotContain(disconnected, manager.ConnectedDevices);
        Assert.Equal(1, disconnected.DisconnectCalls);
        Assert.DoesNotContain(rebooted, manager.ConnectedDevices);
        Assert.Single(rebooted.SentMessages);
        Assert.Equal(1, rebooted.DisconnectCalls);
    }

    /// <summary>Only the device being updated is protected. Another device can still go.</summary>
    [Fact]
    public void During_an_update_another_device_can_still_be_disconnected()
    {
        var manager = NewManager();
        var updating = Connected(manager, "SN-538-E");
        var other = Connected(manager, "SN-538-F");
        manager.DeviceBeingUpdated = updating;

        manager.Disconnect(other);

        Assert.DoesNotContain(other, manager.ConnectedDevices);
        Assert.Contains(updating, manager.ConnectedDevices);
    }

    [Fact]
    public void After_the_update_ends_the_device_can_be_disconnected()
    {
        var manager = NewManager();
        var device = Connected(manager, "SN-538-G");
        manager.DeviceBeingUpdated = device;
        manager.Disconnect(device);

        manager.DeviceBeingUpdated = null;
        manager.Disconnect(device);

        Assert.DoesNotContain(device, manager.ConnectedDevices);
        Assert.Equal(1, device.DisconnectCalls);
    }

    /// <summary>
    /// "Switch to the new device" disconnects the old one first. Disconnect now refuses the device
    /// of an update, so without this check the new connection would open beside the one the update
    /// uses. The manager keeps the device of the update and opens nothing.
    /// </summary>
    [Fact]
    public async Task Switching_to_a_duplicate_keeps_the_device_of_a_firmware_update()
    {
        var manager = NewManager();
        var updating = new DroppableTestDevice("SN-538-DUP", ConnectionType.Usb);
        manager.ConnectedDevices.Add(updating);
        manager.DeviceBeingUpdated = updating;
        manager.DuplicateDeviceHandler = _ => Task.FromResult(DuplicateDeviceAction.SwitchToNew);

        var replacement = new DroppableTestDevice("SN-538-DUP", ConnectionType.Wifi) { PretendConnectSucceeds = true };
        var result = await manager.Connect(replacement);

        Assert.Equal(DAQiFiConnectionStatus.AlreadyConnected, result.Status);
        Assert.Contains(updating, manager.ConnectedDevices);
        Assert.DoesNotContain(replacement, manager.ConnectedDevices);
        Assert.False(replacement.IsConnected);
    }
}
