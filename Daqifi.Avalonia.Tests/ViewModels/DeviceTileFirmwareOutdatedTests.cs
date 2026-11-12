using System.ComponentModel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins the path that shows and hides the amber UPDATE badge on a device tile (issue #568).
///
/// <para>
/// Both views bind the badge's <c>IsVisible</c> to <see cref="DeviceTileViewModel.IsFirmwareOutdated"/>,
/// which reads <see cref="AbstractStreamingDevice.IsFirmwareOutdated"/> and re-raises only when the
/// device raises <see cref="INotifyPropertyChanged.PropertyChanged"/> for that name. The release
/// check sets the flag on the device after the tile already exists (the GitHub request completes
/// after the connection), so without the device's event the tile never reads the new value and the
/// badge stays hidden while the bell says the firmware is outdated.
/// </para>
///
/// <para>
/// The device under test is a real <see cref="AbstractStreamingDevice"/> subclass, not one of the
/// <see cref="IStreamingDevice"/> test doubles: the defect was in the base class's declaration, and
/// a double that implements the event itself would pass whatever the base class does.
/// </para>
/// </summary>
public class DeviceTileFirmwareOutdatedTests
{
    /// <summary>
    /// Minimal concrete <see cref="AbstractStreamingDevice"/>. Nothing here connects or sends;
    /// the property under test is a plain flag on the base class.
    /// </summary>
    private sealed class TestDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message) =>
            throw new NotSupportedException();
    }

    /// <summary>Records each <c>IsFirmwareOutdated</c> raise on the tile, with the value read at that moment.</summary>
    private static List<bool> RecordTileRaises(DeviceTileViewModel tile)
    {
        var seen = new List<bool>();
        tile.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceTileViewModel.IsFirmwareOutdated))
            {
                seen.Add(tile.IsFirmwareOutdated);
            }
        };
        return seen;
    }

    [Fact]
    public void Tile_raises_IsFirmwareOutdated_with_the_new_value_when_the_device_flag_turns_on_and_off()
    {
        var device = new TestDevice();
        using var tile = new DeviceTileViewModel(device);
        var seen = RecordTileRaises(tile);

        // Arrangement guard: the tile starts with the badge hidden, so the first raise below is a
        // real change and not a read of a value that was already true.
        Assert.False(tile.IsFirmwareOutdated);

        device.IsFirmwareOutdated = true;   // release check finds a newer version
        Assert.Equal([true], seen);

        device.IsFirmwareOutdated = false;  // release check after a successful update (#566)
        Assert.Equal([true, false], seen);
    }

    [Fact]
    public void Device_raises_PropertyChanged_for_IsFirmwareOutdated_under_the_interface_name()
    {
        // The tile's switch matches on nameof(IStreamingDevice.IsFirmwareOutdated). Pin the
        // device side directly too, so a rename on either side fails here and not only in the UI.
        var device = new TestDevice();
        var names = new List<string?>();
        device.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        device.IsFirmwareOutdated = true;

        Assert.Contains(nameof(IStreamingDevice.IsFirmwareOutdated), names);
    }

    [Fact]
    public void Device_does_not_raise_when_the_flag_is_set_to_the_value_it_already_has()
    {
        // The release check runs again on every refresh and writes the flag each time. An
        // unchanged value must not make every bound view re-evaluate the badge.
        var device = new TestDevice { IsFirmwareOutdated = true };
        using var tile = new DeviceTileViewModel(device);
        var seen = RecordTileRaises(tile);

        device.IsFirmwareOutdated = true;

        Assert.Empty(seen);
        Assert.True(tile.IsFirmwareOutdated);
    }
}
