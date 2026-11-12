using System.Collections.ObjectModel;
using Daqifi.Core.Firmware;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DesktopStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Issue #569, the notification half: the outdated-firmware notification was text only, and it
/// named the device by a 19-digit serial number. It now names the device the way the tile does and
/// carries an <b>Update</b> button that takes the user to the update control.
/// </summary>
/// <remarks>
/// The notification is made by <see cref="FirmwareUpdateCoordinator.RefreshFirmwareUpdatesAsync"/>,
/// so these tests drive that method with a download service that reports 3.9.0 as the newest
/// release, which is the release the issue was found with.
/// </remarks>
public class OutdatedFirmwareNotificationTests : IDisposable
{
    private readonly RecordingHost _host = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-outdated-notice-tests", Guid.NewGuid().ToString("N"));

    private FirmwareUpdateCoordinator CreateCoordinator() =>
        new(_host,
            new NoFlashUpdateService(),
            new LatestIs390DownloadService(),
            NullLogger<FirmwareUpdateService>.Instance,
            new RecordingAppLogger(),
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: false);

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_notification_has_an_Update_button()
    {
        var device = Nq1("9090539562006014104", "3.8.0");
        _host.SetConnectedDevices(device);

        await CreateCoordinator().RefreshFirmwareUpdatesAsync();

        var notice = Assert.Single(_host.Notifications);
        Assert.Equal("Update", notice.ActionText);
        Assert.NotNull(notice.Action);
    }

    [Fact]
    public async Task The_Update_button_asks_the_host_for_that_device_firmware_section()
    {
        var device = Nq1("9090539562006014104", "3.8.0");
        _host.SetConnectedDevices(device);
        await CreateCoordinator().RefreshFirmwareUpdatesAsync();

        Assert.Empty(_host.FirmwareSectionRequests);
        _host.Notifications.Single().Action!.Execute(null);

        Assert.Same(device, Assert.Single(_host.FirmwareSectionRequests));
    }

    [Fact]
    public async Task The_message_names_the_device_and_both_versions_not_the_serial()
    {
        var device = Nq1("9090539562006014104", "3.8.0");
        _host.SetConnectedDevices(device);

        await CreateCoordinator().RefreshFirmwareUpdatesAsync();

        var message = _host.Notifications.Single().Message;
        Assert.Equal("Nq1 has firmware 3.8.0. Version 3.9.0 is available.", message);
        Assert.DoesNotContain("9090539562006014104", message);
    }

    [Fact]
    public async Task A_friendly_name_is_used_before_the_part_number()
    {
        var device = Nq1("9090539562006014104", "3.8.0");
        device.FriendlyName = "Bench rig";
        _host.SetConnectedDevices(device);

        await CreateCoordinator().RefreshFirmwareUpdatesAsync();

        Assert.StartsWith("Bench rig has firmware 3.8.0.", _host.Notifications.Single().Message);
    }

    /// <summary>Two devices with one name: only the serial tells the user which one it is.</summary>
    [Fact]
    public async Task The_serial_is_added_when_another_connected_device_has_the_same_name()
    {
        var first = Nq1("9090539562006014104", "3.8.0");
        var second = Nq1("9090539562006014105", "3.8.0");
        _host.SetConnectedDevices(first, second);

        await CreateCoordinator().RefreshFirmwareUpdatesAsync();

        var messages = _host.Notifications.Select(n => n.Message).ToList();
        Assert.Contains("Nq1 (serial 9090539562006014104) has firmware 3.8.0. Version 3.9.0 is available.", messages);
        Assert.Contains("Nq1 (serial 9090539562006014105) has firmware 3.8.0. Version 3.9.0 is available.", messages);
    }

    [Fact]
    public async Task A_current_device_gets_no_notification()
    {
        _host.SetConnectedDevices(Nq1("9090539562006014104", "3.9.0"));

        await CreateCoordinator().RefreshFirmwareUpdatesAsync();

        Assert.Empty(_host.Notifications);
    }

    [Fact]
    public async Task The_check_records_the_release_tag_for_the_release_notes_link()
    {
        _host.SetConnectedDevices(Nq1("9090539562006014104", "3.8.0"));
        var coordinator = CreateCoordinator();

        await coordinator.RefreshFirmwareUpdatesAsync();

        Assert.Equal("3.9.0", coordinator.LatestFirmwareVersion);
        Assert.Equal("v3.9.0", coordinator.LatestFirmwareTag);
    }

    #region Helpers and fakes
    private static SerialStreamingDevice Nq1(string serial, string firmwareVersion)
    {
        var device = new SerialStreamingDevice("COM-TEST-569", "DAQiFi Nq1", serial, firmwareVersion);
        device.Metadata.PartNumber = "Nq1";
        return device;
    }

    /// <summary>A host that records what the notification's button asks for.</summary>
    private sealed class RecordingHost : IFirmwareUpdateHost
    {
        private List<DesktopStreamingDevice> _connected = [];

        public List<DesktopStreamingDevice> FirmwareSectionRequests { get; } = [];

        public void SetConnectedDevices(params DesktopStreamingDevice[] devices) => _connected = [.. devices];

        public DesktopStreamingDevice? SelectedDevice => null;
        public IReadOnlyList<DesktopStreamingDevice> ConnectedDevices => _connected;
        public string FirmwareFilePath { get; set; } = string.Empty;
        public bool SelectedDeviceSupportsFirmwareUpdate { set { } }
        public bool IsFirmwareUploading { get; set; }
        public bool IsUploadComplete { set { } }
        public bool HasErrorOccured { set { } }
        public int UploadFirmwareProgress { set { } }
        public int UploadWiFiProgress { set { } }
        public string FirmwareUpdateStatusText { set { } }
        public ObservableCollection<Notifications> Notifications { get; } = [];
        public DesktopStreamingDevice? DeviceBeingUpdated { set { } }
        public void RefreshNotificationCount() { }
        public void ShowFirmwareError(string message) { }
        public void ShowFirmwareUpdateSucceeded() { }
        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void ShowFirmwareSection(DesktopStreamingDevice device) => FirmwareSectionRequests.Add(device);
    }

    /// <summary>
    /// Reports 3.9.0 as the newest release, and an update for every device older than it — the
    /// comparison Core makes, reduced to the two versions these tests use.
    /// </summary>
    private sealed class LatestIs390DownloadService : IFirmwareDownloadService
    {
        private static readonly FirmwareReleaseInfo Latest = new()
        {
            Version = new FirmwareVersion(3, 9, 0, PreLabel: null, PreNumber: 0),
            TagName = "v3.9.0",
            IsPreRelease = false
        };

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<FirmwareReleaseInfo?>(Latest);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString, bool includePreRelease = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FirmwareUpdateCheckResult
            {
                UpdateAvailable = deviceVersionString != "3.9.0",
                LatestRelease = Latest
            });

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory, IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<(string, string)?>(null);

        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory, bool includePreRelease = false, IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName, string destinationDirectory, IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<FirmwareReleaseInfo?>(null);

        public void InvalidateCache() { }
    }

    /// <summary>PIC32 update service that no test here reaches.</summary>
    private sealed class NoFlashUpdateService : IFirmwareUpdateService
    {
        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

#pragma warning disable CS0067 // Part of the interface; nothing in these tests raises it.
        public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device, string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device, string hexFilePath, IProgress<FirmwareUpdateProgress>? progress,
            string? targetDevicePath, string? targetLocationKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateWifiModuleAsync(
            Daqifi.Core.Device.IStreamingDevice device, string firmwarePath,
            IProgress<FirmwareUpdateProgress>? progress = null, CancellationToken cancellationToken = default,
            bool skipVersionCheck = false) => throw new NotSupportedException();

        public Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
            Daqifi.Core.Device.IStreamingDevice device, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
    #endregion
}
