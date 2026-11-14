using Daqifi.Desktop;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.ViewModels;
using Xunit;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #538: during a firmware update, DISCONNECT and REBOOT in the device drawer stayed
/// available. One click disposed the connection that the update uses, and the update failed. The
/// two commands now require that no firmware update is in progress, and the shell commands they
/// call leave the device of the update untouched.
/// </summary>
/// <remarks>
/// In <see cref="ConnectionManagerSingletonCollection"/> because the state is
/// <c>ConnectionManager.Instance.DeviceBeingUpdated</c>. The hop from
/// <c>FirmwareUpdateInProgressChanged</c> to the UI thread is not driven here (the test host has no
/// Avalonia lifetime, so a post may never run); the tests call the step that the hop runs, as
/// <see cref="DeviceTileUpdatingStateTests"/> does.
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class DrawerActionsDuringFirmwareUpdateTests : IDisposable
{
    private readonly List<IStreamingDevice> _registered = [];
    private readonly List<IDisposable> _panes = [];

    public void Dispose()
    {
        ConnectionManager.Instance.DeviceBeingUpdated = null;
        foreach (var pane in _panes) pane.Dispose();
        foreach (var device in _registered)
        {
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); }
            catch { /* best-effort cleanup */ }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Disconnect_and_reboot_are_available_when_no_update_runs()
    {
        var (_, pane, _) = DrawerOpen("COM-TEST-538-A");

        Assert.True(pane.DisconnectSelectedCommand.CanExecute(null));
        Assert.True(pane.RebootSelectedCommand.CanExecute(null));
        Assert.Null(pane.DeviceActionsToolTip);
    }

    [Fact]
    public void Disconnect_and_reboot_are_disabled_during_a_firmware_update()
    {
        var (_, pane, device) = DrawerOpen("COM-TEST-538-B");

        ConnectionManager.Instance.DeviceBeingUpdated = device;
        pane.ApplyFirmwareUpdateState();

        Assert.False(pane.DisconnectSelectedCommand.CanExecute(null));
        Assert.False(pane.RebootSelectedCommand.CanExecute(null));
        Assert.Equal("Not available during a firmware update.", pane.DeviceActionsToolTip);
    }

    [Fact]
    public void Disconnect_and_reboot_become_available_again_when_the_update_ends()
    {
        var (_, pane, device) = DrawerOpen("COM-TEST-538-C");
        ConnectionManager.Instance.DeviceBeingUpdated = device;
        pane.ApplyFirmwareUpdateState();

        ConnectionManager.Instance.DeviceBeingUpdated = null;
        pane.ApplyFirmwareUpdateState();

        Assert.True(pane.DisconnectSelectedCommand.CanExecute(null));
        Assert.True(pane.RebootSelectedCommand.CanExecute(null));
        Assert.Null(pane.DeviceActionsToolTip);
    }

    /// <summary>
    /// The buttons read CanExecute again only when the command says it changed, and the tooltip
    /// only when the property says it changed. Without these the buttons keep the state they were
    /// drawn with.
    /// </summary>
    [Fact]
    public void The_drawer_announces_the_change()
    {
        var (_, pane, device) = DrawerOpen("COM-TEST-538-D");
        var disconnectChanged = 0;
        var rebootChanged = 0;
        var announced = new List<string?>();
        pane.DisconnectSelectedCommand.CanExecuteChanged += (_, _) => disconnectChanged++;
        pane.RebootSelectedCommand.CanExecuteChanged += (_, _) => rebootChanged++;
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        ConnectionManager.Instance.DeviceBeingUpdated = device;
        pane.ApplyFirmwareUpdateState();

        Assert.True(disconnectChanged > 0);
        Assert.True(rebootChanged > 0);
        Assert.Contains(nameof(DevicesPaneViewModel.DeviceActionsToolTip), announced);
    }

    /// <summary>
    /// A drawer opened while an update already runs — the device list is rebuilt when the device
    /// reconnects after the write — starts with the two commands disabled.
    /// </summary>
    [Fact]
    public void A_drawer_opened_during_an_update_starts_disabled()
    {
        var device = Register(new SerialStreamingDevice("COM-TEST-538-E"));
        ConnectionManager.Instance.DeviceBeingUpdated = device;
        var pane = Track(new DevicesPaneViewModel(new DaqifiViewModel(new NullDialogService())));

        pane.OpenSettingsCommand.Execute(new DeviceTileViewModel(device));

        Assert.False(pane.DisconnectSelectedCommand.CanExecute(null));
        Assert.False(pane.RebootSelectedCommand.CanExecute(null));
    }

    /// <summary>
    /// The barrier behind the buttons: a caller that runs the shell command anyway leaves the
    /// device in the list and in the selection. The WiFi-only update reads <c>SelectedDevice</c>
    /// after the write, so a cleared selection made a good write report a failure.
    /// </summary>
    [Fact]
    public void The_shell_disconnect_command_leaves_the_device_of_an_update_alone()
    {
        var (shell, _, device) = DrawerOpen("COM-TEST-538-F");
        ConnectionManager.Instance.DeviceBeingUpdated = device;

        shell.DisconnectDeviceCommand.Execute(device);

        Assert.Contains(device, ConnectionManager.Instance.ConnectedDevices);
        Assert.Same(device, shell.SelectedDevice);
    }

    [Fact]
    public void The_shell_reboot_command_leaves_the_device_of_an_update_alone()
    {
        var (shell, _, device) = DrawerOpen("COM-TEST-538-G");
        ConnectionManager.Instance.DeviceBeingUpdated = device;

        shell.RebootDeviceCommand.Execute(device);

        Assert.Contains(device, ConnectionManager.Instance.ConnectedDevices);
        Assert.Same(device, shell.SelectedDevice);
    }

    private (DaqifiViewModel Shell, DevicesPaneViewModel Pane, SerialStreamingDevice Device) DrawerOpen(string port)
    {
        var device = Register(new SerialStreamingDevice(port));
        var shell = new DaqifiViewModel(new NullDialogService());
        var pane = Track(new DevicesPaneViewModel(shell));
        pane.OpenSettingsCommand.Execute(new DeviceTileViewModel(device));
        return (shell, pane, device);
    }

    private DevicesPaneViewModel Track(DevicesPaneViewModel pane)
    {
        _panes.Add(pane);
        return pane;
    }

    private T Register<T>(T device) where T : IStreamingDevice
    {
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
        return device;
    }
}
