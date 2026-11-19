using Daqifi.Desktop;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.ViewModels;
using Xunit;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #643: during a firmware update the device drawer disabled REBOOT and DISCONNECT (#538),
/// but APPLY NETWORK SETTINGS, SAVE NAME, BROWSE, the logging mode, the frequency and the firmware
/// file stayed enabled. APPLY then said "the device is not connected" while the header said
/// "1 CONNECTED", and the red line stayed after the update. The tile also showed UPDATE next to
/// UPDATING.
/// </summary>
/// <remarks>
/// In <see cref="ConnectionManagerSingletonCollection"/> because the state is
/// <c>ConnectionManager.Instance.DeviceBeingUpdated</c>. As in
/// <see cref="DrawerActionsDuringFirmwareUpdateTests"/>, the hop from
/// <c>FirmwareUpdateInProgressChanged</c> to the UI thread is not driven (the test host has no
/// Avalonia lifetime); the tests call the step that the hop runs.
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class DrawerCommandsDuringFirmwareUpdateTests : IDisposable
{
    private const string NotConnected = "Cannot apply WiFi settings — the device is not connected.";

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
    public void Drawer_commands_are_available_when_no_update_runs()
    {
        var (shell, pane, _) = DrawerOpen("COM-TEST-643-A");

        Assert.True(shell.UpdateNetworkConfigurationCommand.CanExecute(null));
        Assert.True(shell.SetFriendlyNameCommand.CanExecute(null));
        Assert.True(shell.BrowseForFirmwareCommand.CanExecute(null));
        Assert.True(pane.SetLoggingModeCommand.CanExecute("Stream to App"));
        Assert.True(pane.CanUseDeviceCommands);
        Assert.Null(pane.DeviceActionsToolTip);
    }

    /// <summary>The issue's first check: APPLY is disabled during the update, and enabled after it.</summary>
    [Fact]
    public void Apply_network_settings_is_disabled_during_an_update_and_enabled_after_it()
    {
        var (shell, pane, device) = DrawerOpen("COM-TEST-643-B");

        StartUpdate(pane, device);
        Assert.False(shell.UpdateNetworkConfigurationCommand.CanExecute(null));

        EndUpdate(pane);
        Assert.True(shell.UpdateNetworkConfigurationCommand.CanExecute(null));
    }

    [Fact]
    public void Every_drawer_control_that_talks_to_the_device_is_disabled_during_an_update()
    {
        var (shell, pane, device) = DrawerOpen("COM-TEST-643-C");

        StartUpdate(pane, device);

        Assert.False(shell.SetFriendlyNameCommand.CanExecute(null));
        Assert.False(shell.BrowseForFirmwareCommand.CanExecute(null));
        Assert.False(pane.SetLoggingModeCommand.CanExecute("Log to Device"));
        Assert.False(pane.CanUseDeviceCommands);
        Assert.Equal("Not available during a firmware update.", pane.DeviceActionsToolTip);
    }

    [Fact]
    public void Every_drawer_control_is_enabled_again_when_the_update_ends()
    {
        var (shell, pane, device) = DrawerOpen("COM-TEST-643-D");
        StartUpdate(pane, device);

        EndUpdate(pane);

        Assert.True(shell.SetFriendlyNameCommand.CanExecute(null));
        Assert.True(shell.BrowseForFirmwareCommand.CanExecute(null));
        Assert.True(pane.SetLoggingModeCommand.CanExecute("Log to Device"));
        Assert.True(pane.CanUseDeviceCommands);
        Assert.Null(pane.DeviceActionsToolTip);
    }

    /// <summary>
    /// CANCEL UPDATE is the control that stops an update, so the new condition must not disable it.
    /// </summary>
    [Fact]
    public void Cancel_update_stays_available_during_an_update()
    {
        var (shell, pane, device) = DrawerOpen("COM-TEST-643-E");
        shell.IsFirmwareUploading = true;

        StartUpdate(pane, device);

        Assert.True(shell.CancelFirmwareUploadCommand.CanExecute(null));
    }

    /// <summary>
    /// A button reads CanExecute again only when its command says that it changed, and a bound
    /// property only when it is announced. Without these the controls keep the state they were
    /// drawn with.
    /// </summary>
    [Fact]
    public void The_drawer_announces_the_change_when_an_update_starts_and_ends()
    {
        var (shell, pane, device) = DrawerOpen("COM-TEST-643-F");
        var raised = new Dictionary<string, int>
        {
            ["apply"] = 0, ["name"] = 0, ["browse"] = 0, ["mode"] = 0,
        };
        shell.UpdateNetworkConfigurationCommand.CanExecuteChanged += (_, _) => raised["apply"]++;
        shell.SetFriendlyNameCommand.CanExecuteChanged += (_, _) => raised["name"]++;
        shell.BrowseForFirmwareCommand.CanExecuteChanged += (_, _) => raised["browse"]++;
        pane.SetLoggingModeCommand.CanExecuteChanged += (_, _) => raised["mode"]++;
        var announced = new List<string?>();
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        StartUpdate(pane, device);
        Assert.All(raised, r => Assert.True(r.Value >= 1, $"{r.Key} not raised at start"));
        Assert.Contains(nameof(DevicesPaneViewModel.CanUseDeviceCommands), announced);

        // Count the end on its own. The start can raise each one more than once (the setter's own
        // handler and the step StartUpdate runs), so a running total cannot show the end raised it.
        foreach (var key in raised.Keys.ToList()) raised[key] = 0;
        announced.Clear();

        EndUpdate(pane);
        Assert.All(raised, r => Assert.True(r.Value >= 1, $"{r.Key} not raised at end"));
        Assert.Contains(nameof(DevicesPaneViewModel.CanUseDeviceCommands), announced);
    }

    /// <summary>
    /// The NAME box's rule message (issue #596) is about the text in the box, not a device result,
    /// so an update must not erase it: SAVE NAME stays disabled for that text, and the line says why.
    /// </summary>
    [Fact]
    public void An_update_keeps_the_rule_message_for_an_invalid_name_in_the_box()
    {
        var (shell, pane, device) = DrawerOpen("COM-TEST-643-M");
        shell.PendingFriendlyName = string.Empty;
        var rule = shell.FriendlyNameError;
        Assert.False(string.IsNullOrEmpty(rule), "Arrangement: an empty name must have a rule message.");

        StartUpdate(pane, device);
        Assert.Equal(rule, shell.FriendlyNameError);

        EndUpdate(pane);
        Assert.Equal(rule, shell.FriendlyNameError);
        Assert.False(shell.SetFriendlyNameCommand.CanExecute(null));
    }

    /// <summary>
    /// The FREQUENCY slider is disabled during an update; a write that reaches the setter anyway (a
    /// delayed binding update) must not reach the device, and the slider is told to move back.
    /// </summary>
    [Fact]
    public void A_frequency_write_during_an_update_does_not_reach_the_device()
    {
        var (shell, pane, device) = DrawerOpen("COM-TEST-643-N");
        // The shell writes a rate only to a device in its connected list.
        shell.ConnectedDevices.Add(device);
        pane.FrequencyHz = 10;
        Assert.Equal(10, device.StreamingFrequency); // Arrangement: outside an update, the write lands.
        StartUpdate(pane, device);
        var announced = new List<string?>();
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        pane.FrequencyHz = 20;

        Assert.Equal(10, device.StreamingFrequency);
        Assert.Contains(nameof(DevicesPaneViewModel.FrequencyHz), announced);
    }

    /// <summary>The issue's second check: an error from before the update does not survive it.</summary>
    [Fact]
    public void A_status_line_from_before_the_update_is_cleared_when_it_starts()
    {
        var (shell, pane, device) = DrawerOpen("COM-TEST-643-G");
        shell.NetworkSettingsError = NotConnected;
        shell.FriendlyNameError = "Cannot set the device name — the device is not connected.";

        StartUpdate(pane, device);

        Assert.False(shell.HasNetworkSettingsError);
        Assert.False(shell.HasFriendlyNameError);
    }

    /// <summary>
    /// The bench case: the error appeared during the update and stayed after the Success dialog
    /// closed, while the device was connected again.
    /// </summary>
    [Fact]
    public void A_status_line_set_during_the_update_is_cleared_when_it_ends()
    {
        var (shell, pane, device) = DrawerOpen("COM-TEST-643-H");
        StartUpdate(pane, device);
        shell.NetworkSettingsError = NotConnected;
        shell.NetworkSettingsApplied = true;
        shell.FriendlyNameApplied = true;

        EndUpdate(pane);

        Assert.False(shell.HasNetworkSettingsError);
        Assert.False(shell.NetworkSettingsApplied);
        Assert.False(shell.FriendlyNameApplied);
    }

    /// <summary>
    /// The pane also runs the step after each rebuild of its tiles (a device connects or leaves).
    /// Only a start or an end of an update clears the lines; another rebuild leaves a fresh result.
    /// </summary>
    [Fact]
    public void A_rebuild_without_an_update_leaves_the_status_lines_alone()
    {
        var (shell, pane, _) = DrawerOpen("COM-TEST-643-I");
        shell.NetworkSettingsError = NotConnected;

        pane.ApplyFirmwareUpdateState();

        Assert.Equal(NotConnected, shell.NetworkSettingsError);
    }

    /// <summary>
    /// A drawer opened while an update already runs — the device list is rebuilt when the device
    /// reconnects after the write — starts with the controls disabled.
    /// </summary>
    [Fact]
    public void A_drawer_opened_during_an_update_starts_disabled()
    {
        var device = Register(new SerialStreamingDevice("COM-TEST-643-J"));
        ConnectionManager.Instance.DeviceBeingUpdated = device;
        var shell = new DaqifiViewModel(new NullDialogService());
        var pane = Track(new DevicesPaneViewModel(shell));

        pane.OpenSettingsCommand.Execute(new DeviceTileViewModel(device));
        shell.PendingFriendlyName = "Lab Nyquist";

        Assert.False(shell.UpdateNetworkConfigurationCommand.CanExecute(null));
        Assert.False(shell.SetFriendlyNameCommand.CanExecute(null));
        Assert.False(pane.CanUseDeviceCommands);
    }

    /// <summary>The tile shows UPDATING in place of UPDATE while the update runs.</summary>
    [Fact]
    public void The_tile_hides_update_while_updating_shows()
    {
        var updated = Register(new SerialStreamingDevice("COM-TEST-643-K"));
        updated.IsFirmwareOutdated = true;
        using var pane = new DevicesPaneViewModel(shell: null);
        var tile = pane.Devices.Single(t => ReferenceEquals(t.Device, updated));
        Assert.True(tile.ShowsUpdateAvailable);
        var announced = new List<string?>();
        tile.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        ConnectionManager.Instance.DeviceBeingUpdated = updated;
        pane.ApplyFirmwareUpdateState();

        Assert.True(tile.IsUpdatingFirmware);
        Assert.False(tile.ShowsUpdateAvailable);
        Assert.Contains(nameof(DeviceTileViewModel.ShowsUpdateAvailable), announced);

        ConnectionManager.Instance.DeviceBeingUpdated = null;
        pane.ApplyFirmwareUpdateState();

        Assert.True(tile.ShowsUpdateAvailable);
    }

    [Fact]
    public void The_tile_shows_update_only_for_outdated_firmware()
    {
        var device = new SerialStreamingDevice("COM-TEST-643-L");
        using var tile = new DeviceTileViewModel(device);
        Assert.False(tile.ShowsUpdateAvailable);

        device.IsFirmwareOutdated = true;

        Assert.True(tile.ShowsUpdateAvailable);
    }

    private static void StartUpdate(DevicesPaneViewModel pane, IStreamingDevice device)
    {
        ConnectionManager.Instance.DeviceBeingUpdated = device;
        pane.ApplyFirmwareUpdateState();
    }

    private static void EndUpdate(DevicesPaneViewModel pane)
    {
        ConnectionManager.Instance.DeviceBeingUpdated = null;
        pane.ApplyFirmwareUpdateState();
    }

    private (DaqifiViewModel Shell, DevicesPaneViewModel Pane, SerialStreamingDevice Device) DrawerOpen(string port)
    {
        var device = Register(new SerialStreamingDevice(port));
        var shell = new DaqifiViewModel(new NullDialogService());
        var pane = Track(new DevicesPaneViewModel(shell));
        pane.OpenSettingsCommand.Execute(new DeviceTileViewModel(device));
        // A name the device accepts, so SAVE NAME depends only on the update state.
        shell.PendingFriendlyName = "Lab Nyquist";
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
