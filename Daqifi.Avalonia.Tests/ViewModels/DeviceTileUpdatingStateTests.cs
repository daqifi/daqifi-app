using Daqifi.Desktop;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.ViewModels;
using Xunit;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #570, point 5: the tile of the device being updated shows an UPDATING state. During the
/// write the device is in its bootloader, and the tile used to show the green "connected" dot and
/// the old version with nothing to say an update was running.
/// </summary>
/// <remarks>
/// In <see cref="ConnectionManagerSingletonCollection"/> because the state comes from
/// <c>ConnectionManager.Instance.DeviceBeingUpdated</c>, which the connection dialog reads as the
/// app-wide "firmware update running" gate, and <see cref="DevicesPaneViewModel"/> enumerates the
/// singleton's device list. The event hop (<c>FirmwareUpdateInProgressChanged</c> to the UI thread)
/// is not driven here: the test host has no Avalonia lifetime, so a post to the UI thread may never
/// run. These tests call the step the hop runs, and the PR's screenshot shows the hop in the app.
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class DeviceTileUpdatingStateTests : IDisposable
{
    private readonly List<IStreamingDevice> _registered = [];

    public void Dispose()
    {
        ConnectionManager.Instance.DeviceBeingUpdated = null;
        foreach (var device in _registered)
        {
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); }
            catch { /* best-effort cleanup */ }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_tile_is_not_updating_by_default()
    {
        Assert.False(new DeviceTileViewModel(new SerialStreamingDevice("COM-TEST-570")).IsUpdatingFirmware);
    }

    /// <summary>Only the device being updated is marked; the other tile is not.</summary>
    [Fact]
    public void Only_the_tile_of_the_device_being_updated_shows_updating()
    {
        var updated = Register(new SerialStreamingDevice("COM-TEST-570-A"));
        var other = Register(new SerialStreamingDevice("COM-TEST-570-B"));
        using var pane = new DevicesPaneViewModel(shell: null);

        ConnectionManager.Instance.DeviceBeingUpdated = updated;
        pane.ApplyFirmwareUpdateState();

        Assert.True(TileFor(pane, updated).IsUpdatingFirmware);
        Assert.False(TileFor(pane, other).IsUpdatingFirmware);
    }

    /// <summary>The state goes away when the update ends — the guard in the other direction.</summary>
    [Fact]
    public void The_updating_state_clears_when_the_update_ends()
    {
        var device = Register(new SerialStreamingDevice("COM-TEST-570-C"));
        using var pane = new DevicesPaneViewModel(shell: null);
        ConnectionManager.Instance.DeviceBeingUpdated = device;
        pane.ApplyFirmwareUpdateState();
        Assert.True(TileFor(pane, device).IsUpdatingFirmware);

        ConnectionManager.Instance.DeviceBeingUpdated = null;
        pane.ApplyFirmwareUpdateState();

        Assert.False(TileFor(pane, device).IsUpdatingFirmware);
    }

    /// <summary>
    /// The device list is rebuilt during an update (the device reconnects after the flash), and a
    /// pane built mid-update starts from the current state. A fresh tile must not lose it.
    /// </summary>
    [Fact]
    public void A_tile_built_during_an_update_shows_updating()
    {
        var device = Register(new SerialStreamingDevice("COM-TEST-570-D"));
        ConnectionManager.Instance.DeviceBeingUpdated = device;

        using var pane = new DevicesPaneViewModel(shell: null);

        Assert.True(TileFor(pane, device).IsUpdatingFirmware);
    }

    /// <summary>The binding needs the change announced, or the pill never appears.</summary>
    [Fact]
    public void The_tile_announces_the_change()
    {
        var tile = new DeviceTileViewModel(new SerialStreamingDevice("COM-TEST-570-E"));
        var announced = new List<string?>();
        tile.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        tile.IsUpdatingFirmware = true;

        Assert.Contains(nameof(DeviceTileViewModel.IsUpdatingFirmware), announced);
    }

    private static DeviceTileViewModel TileFor(DevicesPaneViewModel pane, IStreamingDevice device) =>
        Assert.Single(pane.Devices, tile => ReferenceEquals(tile.Device, device));

    private T Register<T>(T device) where T : IStreamingDevice
    {
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
        return device;
    }
}
