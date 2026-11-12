using Daqifi.Desktop;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.ViewModels;
using Xunit;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #569, the drawer half: the FIRMWARE section of the Devices drawer showed a file box, a
/// progress bar and an <b>UPDATE</b> button, and no version at all. The app knew the installed
/// version and the newest release; the drawer used neither.
/// </summary>
/// <remarks>
/// <para>
/// The drawer binds these members with compiled bindings (<c>x:CompileBindings="True"</c>), so the
/// compiler checks that each one exists. What it cannot check is the value and the announcement, and
/// those are what these tests pin. The text is in the view model, not in XAML, so the tests read the
/// exact words the user sees.
/// </para>
/// <para>
/// In <see cref="ConnectionManagerSingletonCollection"/> because <see cref="DevicesPaneViewModel"/>'s
/// constructor subscribes to <c>ConnectionManager.Instance</c> and enumerates its device list.
/// </para>
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class FirmwareUpdateAvailabilityTests : IDisposable
{
    private readonly List<IStreamingDevice> _registered = [];
    private readonly List<IDisposable> _panes = [];

    public void Dispose()
    {
        foreach (var pane in _panes) pane.Dispose();
        foreach (var device in _registered)
        {
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); }
            catch { /* best-effort cleanup */ }
        }

        GC.SuppressFinalize(this);
    }

    #region What the FIRMWARE section says
    [Fact]
    public void An_outdated_device_shows_both_versions_and_an_update_to_the_newest()
    {
        var (shell, pane, _) = DrawerOpenOn("3.8.0", outdated: true);
        shell.SetLatestFirmwareRelease("3.9.0", "v3.9.0");

        Assert.True(pane.SelectedDeviceHasFirmwareUpdate);
        Assert.False(pane.SelectedDeviceFirmwareIsCurrent);
        Assert.Equal("Installed 3.8.0. Version 3.9.0 is available.", pane.FirmwareStatusText);
        Assert.Equal("UPDATE TO 3.9.0", pane.UpdateFirmwareButtonText);
    }

    [Fact]
    public void A_current_device_says_it_is_up_to_date()
    {
        var (shell, pane, _) = DrawerOpenOn("3.9.0", outdated: false);
        shell.SetLatestFirmwareRelease("3.9.0", "v3.9.0");

        Assert.False(pane.SelectedDeviceHasFirmwareUpdate);
        Assert.True(pane.SelectedDeviceFirmwareIsCurrent);
        Assert.Equal("Installed 3.9.0. Firmware is up to date.", pane.FirmwareStatusText);
        Assert.Equal("INSTALL 3.9.0", pane.UpdateFirmwareButtonText);
    }

    /// <summary>
    /// Before the check has succeeded (or when it failed) the drawer says only what it knows. It
    /// must not say "up to date": nothing has been compared.
    /// </summary>
    [Fact]
    public void Before_the_check_the_drawer_states_only_the_installed_version()
    {
        var (_, pane, _) = DrawerOpenOn("3.8.0", outdated: false);

        Assert.False(pane.SelectedDeviceHasFirmwareUpdate);
        Assert.False(pane.SelectedDeviceFirmwareIsCurrent);
        Assert.Equal("Installed 3.8.0.", pane.FirmwareStatusText);
        Assert.Equal("UPDATE", pane.UpdateFirmwareButtonText);
        Assert.Null(pane.FirmwareReleaseNotesUri);
    }

    /// <summary>
    /// A chosen file is what the button writes (<c>UploadFirmwareAsync</c> tests the same path), so
    /// the button must not name a version it will not install.
    /// </summary>
    [Fact]
    public void A_chosen_file_puts_the_file_on_the_button_not_the_version()
    {
        var (shell, pane, _) = DrawerOpenOn("3.8.0", outdated: true);
        shell.SetLatestFirmwareRelease("3.9.0", "v3.9.0");

        shell.FirmwareFilePath = "/tmp/daqifi-569/firmware.hex";

        Assert.Equal("UPDATE FROM FILE", pane.UpdateFirmwareButtonText);
    }

    [Fact]
    public void The_release_notes_link_goes_to_the_newest_release()
    {
        var (shell, pane, _) = DrawerOpenOn("3.8.0", outdated: true);
        shell.SetLatestFirmwareRelease("3.9.0", "v3.9.0");

        Assert.Equal(
            new Uri("https://github.com/daqifi/daqifi-nyquist-firmware/releases/tag/v3.9.0"),
            pane.FirmwareReleaseNotesUri);
        Assert.Equal("Release notes for 3.9.0", pane.FirmwareReleaseNotesText);
    }
    #endregion

    #region The drawer is told
    /// <summary>
    /// The check finishes seconds after the device connects, often with the drawer already open.
    /// The drawer's values are computed, so without an announcement it keeps the text it was drawn
    /// with: "Installed 3.8.0." and no update.
    /// </summary>
    [Fact]
    public void An_open_drawer_is_told_when_the_check_finishes()
    {
        var (shell, pane, _) = DrawerOpenOn("3.8.0", outdated: true);
        var announced = new List<string?>();
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        shell.SetLatestFirmwareRelease("3.9.0", "v3.9.0");

        Assert.Contains(nameof(DevicesPaneViewModel.SelectedDeviceHasFirmwareUpdate), announced);
        Assert.Contains(nameof(DevicesPaneViewModel.SelectedDeviceFirmwareIsCurrent), announced);
        Assert.Contains(nameof(DevicesPaneViewModel.FirmwareStatusText), announced);
        Assert.Contains(nameof(DevicesPaneViewModel.UpdateFirmwareButtonText), announced);
        Assert.Contains(nameof(DevicesPaneViewModel.FirmwareReleaseNotesUri), announced);
        Assert.Contains(nameof(DevicesPaneViewModel.FirmwareReleaseNotesText), announced);
    }

    [Fact]
    public void Choosing_a_file_relabels_the_button()
    {
        var (shell, pane, _) = DrawerOpenOn("3.8.0", outdated: true);
        var announced = new List<string?>();
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        shell.FirmwareFilePath = "/tmp/daqifi-569/firmware.hex";

        Assert.Contains(nameof(DevicesPaneViewModel.UpdateFirmwareButtonText), announced);
    }

    [Fact]
    public void A_disposed_pane_stops_listening_to_the_shell()
    {
        var (shell, pane, _) = DrawerOpenOn("3.8.0", outdated: true);
        var announced = new List<string?>();
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName);
        pane.Dispose();

        shell.SetLatestFirmwareRelease("3.9.0", "v3.9.0");

        Assert.Empty(announced);
    }
    #endregion

    #region The notification's Update button
    /// <summary>
    /// The Devices pane is on screen: the request opens the drawer of that device and asks the view
    /// to scroll to FIRMWARE — once.
    /// </summary>
    [Fact]
    public void ShowFirmwareSection_opens_the_drawer_of_that_device_at_firmware()
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var pane = Track(new DevicesPaneViewModel(shell));
        var other = Nq1("9090539562006014105", "3.8.0");
        var device = Nq1("9090539562006014104", "3.8.0");
        pane.Devices.Add(new DeviceTileViewModel(other));
        pane.Devices.Add(new DeviceTileViewModel(device));
        shell.IsNotificationsOpen = true;
        var shown = 0;
        pane.FirmwareSectionShown += (_, _) => shown++;

        shell.ShowFirmwareSection(device);

        Assert.Equal(DaqifiViewModel.DevicesTabIndex, shell.SelectedIndex);
        Assert.False(shell.IsNotificationsOpen);
        Assert.True(pane.IsSettingsOpen);
        Assert.Same(device, pane.SelectedDevice);
        Assert.Same(device, shell.SelectedDevice);
        Assert.Equal(1, shown);
        Assert.True(pane.TakeFirmwareScrollRequest());
        Assert.False(pane.TakeFirmwareScrollRequest());
    }

    /// <summary>
    /// The usual case: the user is on another tab, so the Devices pane does not exist yet. The tab
    /// change builds it, and the pane takes the request when it is built.
    /// </summary>
    [Fact]
    public void A_pane_built_after_the_request_opens_the_drawer()
    {
        var device = Nq1("9090539562006014104", "3.8.0");
        Register(device);
        var shell = new DaqifiViewModel(new NullDialogService());

        shell.ShowFirmwareSection(device);
        var pane = Track(new DevicesPaneViewModel(shell));

        Assert.True(pane.IsSettingsOpen);
        Assert.Same(device, pane.SelectedDevice);
        Assert.True(pane.TakeFirmwareScrollRequest());
    }

    /// <summary>A device that reconnected is a new wrapper with the same serial; the request finds it.</summary>
    [Fact]
    public void The_request_finds_a_reconnected_device_by_its_serial()
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var pane = Track(new DevicesPaneViewModel(shell));
        var before = Nq1("9090539562006014104", "3.8.0");
        var reconnected = Nq1("9090539562006014104", "3.8.0");
        pane.Devices.Add(new DeviceTileViewModel(reconnected));

        shell.ShowFirmwareSection(before);

        Assert.Same(reconnected, pane.SelectedDevice);
    }

    /// <summary>A request is taken once; a pane built later must not open a drawer for an old click.</summary>
    [Fact]
    public void A_request_is_taken_only_once()
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var pane = Track(new DevicesPaneViewModel(shell));
        var device = Nq1("9090539562006014104", "3.8.0");
        pane.Devices.Add(new DeviceTileViewModel(device));
        shell.ShowFirmwareSection(device);

        Assert.Null(shell.TakeFirmwareSectionRequest());
    }

    [Fact]
    public void A_device_that_left_opens_no_drawer()
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var pane = Track(new DevicesPaneViewModel(shell));

        shell.ShowFirmwareSection(Nq1("9090539562006014104", "3.8.0"));

        Assert.Equal(DaqifiViewModel.DevicesTabIndex, shell.SelectedIndex);
        Assert.False(pane.IsSettingsOpen);
        Assert.False(pane.TakeFirmwareScrollRequest());
    }
    #endregion

    #region Helpers
    private (DaqifiViewModel Shell, DevicesPaneViewModel Pane, SerialStreamingDevice Device) DrawerOpenOn(
        string installed, bool outdated)
    {
        var shell = new DaqifiViewModel(new NullDialogService());
        var pane = Track(new DevicesPaneViewModel(shell));
        var device = Nq1("9090539562006014104", installed);
        // The flag the version check sets on each device before it announces the newest release.
        device.IsFirmwareOutdated = outdated;
        pane.OpenSettingsCommand.Execute(new DeviceTileViewModel(device));
        return (shell, pane, device);
    }

    private static SerialStreamingDevice Nq1(string serial, string firmwareVersion)
    {
        var device = new SerialStreamingDevice("COM-TEST-569", "DAQiFi Nq1", serial, firmwareVersion);
        device.Metadata.PartNumber = "Nq1";
        return device;
    }

    private DevicesPaneViewModel Track(DevicesPaneViewModel pane)
    {
        _panes.Add(pane);
        return pane;
    }

    private void Register(IStreamingDevice device)
    {
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
    }
    #endregion
}
