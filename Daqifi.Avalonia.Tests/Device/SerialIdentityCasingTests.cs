using System.Collections.ObjectModel;
using Daqifi.Core.Firmware;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DesktopStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins issue #339: the serial number is the identity key for notification de-duplication, and the
/// two files that asked "is there already a notification for this device?" answered it differently
/// — <see cref="FirmwareUpdateCoordinator"/> compared ordinally with <c>==</c>, while the newer
/// <c>DaqifiViewModel</c> notification code used <see cref="StringComparison.OrdinalIgnoreCase"/>.
///
/// <para>
/// The fix is one named rule, <see cref="DeviceSerials.SameDevice"/>, so that the next site cannot
/// pick a third convention. These tests pin the rule itself and then the three coordinator call
/// sites whose behaviour it changes, each through a public entry point rather than the private
/// method — a test that only exercised the helper would still pass if a site drifted back to
/// <c>==</c>.
/// </para>
/// </summary>
public class SerialIdentityCasingTests : IDisposable
{
    private readonly RecordingHost _host = new();
    private readonly StubDownloadService _downloads = new();
    private readonly StubUpdateService _updates = new();
    private readonly RecordingAppLogger _logger = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-serial-casing-tests", Guid.NewGuid().ToString("N"));

    private FirmwareUpdateCoordinator CreateCoordinator(bool canFlashWifiModule = false) =>
        new(_host,
            _updates,
            _downloads,
            NullLogger<FirmwareUpdateService>.Instance,
            _logger,
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: canFlashWifiModule);

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #region The rule
    /// <summary>
    /// The defect in one line: two spellings of one board's serial are one device.
    /// </summary>
    [Theory]
    [InlineData("7e2815916200e898", "7E2815916200E898")]
    [InlineData("sn-0001", "SN-0001")]
    [InlineData("SN-0001", "SN-0001")]
    public void Serials_differing_only_in_case_name_the_same_device(string left, string right)
    {
        Assert.True(DeviceSerials.SameDevice(left, right));
    }

    /// <summary>
    /// The negative control, and the answer to "could case-folding merge two real boards?". It can
    /// only ever collapse serials that differ in case alone; two genuinely distinct devices differ
    /// in some character that is not a case variant of the other's, and stay distinct.
    /// </summary>
    [Theory]
    [InlineData("SN-0001", "SN-0002")]
    [InlineData("9090539562006014104", "9090539562006014105")]
    [InlineData("SN-0001", "SN-0001 ")]
    [InlineData("SN-0001", "")]
    public void Serials_that_differ_by_more_than_case_stay_distinct(string left, string right)
    {
        Assert.False(DeviceSerials.SameDevice(left, right));
    }

    /// <summary>
    /// Null-safety, which the sites relied on before: they compared with <c>==</c> on a property
    /// that is <c>string?</c>, so a missing serial had to be a false rather than a throw. The
    /// helper is the static <see cref="string.Equals(string?, string?, StringComparison)"/>, which
    /// dereferences neither side, so it answers the same way and introduces no
    /// <see cref="NullReferenceException"/> where there was none.
    /// </summary>
    [Fact]
    public void A_missing_serial_is_compared_rather_than_dereferenced()
    {
        Assert.False(DeviceSerials.SameDevice(null, "SN-0001"));
        Assert.False(DeviceSerials.SameDevice("SN-0001", null));

        // Two nulls were equal under == and stay equal here. Deciding that a missing serial is no
        // identity at all is each caller's own guard, deliberately left outside this helper so
        // routing a site through it cannot silently change which notifications it matches.
        Assert.True(DeviceSerials.SameDevice(null, null));
    }
    #endregion

    #region The coordinator sites the rule changes
    /// <summary>
    /// <c>AddNotification</c>, reached through <see cref="FirmwareUpdateCoordinator.RefreshFirmwareUpdatesAsync"/>:
    /// a standing outdated-firmware notice must be recognised as this device's when the serial comes
    /// back cased differently, or the flyout stacks a second copy of the same notice.
    /// </summary>
    [Fact]
    public async Task An_outdated_firmware_notice_is_not_stacked_by_a_differently_cased_serial()
    {
        _host.Notifications.Add(new Notifications
        {
            DeviceSerialNo = "sn-0001",
            Message = "Device With Serial sn-0001 has Outdated Firmware. Please Update to Version 3.9.0.",
            IsFirmwareUpdate = true
        });

        _host.SetConnectedDevices(Device("SN-0001"));

        await CreateCoordinator().RefreshFirmwareUpdatesAsync();

        Assert.Single(_host.Notifications);
    }

    /// <summary>
    /// The negative control for the one above: the dedup is still per device, so a second board
    /// keeps its own notice. Case-folding must not be allowed to widen into "one notice for all".
    /// </summary>
    [Fact]
    public async Task A_second_device_still_gets_its_own_outdated_firmware_notice()
    {
        _host.Notifications.Add(new Notifications
        {
            DeviceSerialNo = "sn-0001",
            Message = "Device With Serial sn-0001 has Outdated Firmware. Please Update to Version 3.9.0.",
            IsFirmwareUpdate = true
        });

        _host.SetConnectedDevices(Device("SN-0002"));

        await CreateCoordinator().RefreshFirmwareUpdatesAsync();

        Assert.Equal(2, _host.Notifications.Count);
    }

    /// <summary>
    /// <c>RemoveFirmwareNotification</c>, which is public. A device whose firmware is now current,
    /// or which has disconnected, must clear its own notice even if the serial it is carrying this
    /// time is cased differently — otherwise the notice is stranded in the flyout for the rest of
    /// the session with no device behind it.
    /// </summary>
    [Fact]
    public void A_differently_cased_serial_still_clears_that_devices_firmware_notice()
    {
        _host.Notifications.Add(new Notifications
        {
            DeviceSerialNo = "sn-0001",
            Message = "Device With Serial sn-0001 has Outdated Firmware. Please Update to Version 3.9.0.",
            IsFirmwareUpdate = true
        });

        CreateCoordinator().RemoveFirmwareNotification(Device("SN-0001"));

        Assert.Empty(_host.Notifications);
        Assert.True(_host.NotificationCountRefreshed, "the notification badge was not re-synced.");
    }

    /// <summary>
    /// The negative control: removal is still scoped to the device that asked, so another board's
    /// notice survives.
    /// </summary>
    [Fact]
    public void Removing_one_devices_notice_leaves_another_devices_standing()
    {
        _host.Notifications.Add(new Notifications
        {
            DeviceSerialNo = "sn-0001", Message = "outdated", IsFirmwareUpdate = true
        });

        CreateCoordinator().RemoveFirmwareNotification(Device("SN-0002"));

        Assert.Single(_host.Notifications);
    }

    /// <summary>
    /// <c>NotifyWifiModuleWasNotUpdated</c>, reached through
    /// <see cref="FirmwareUpdateCoordinator.UpdateWifiModuleOnlyAsync"/> on a machine that cannot
    /// flash the WINC module. It is a standing limitation of the machine rather than an event, so
    /// two runs against one board leave one notice — including when the board reports its serial
    /// cased differently the second time. <c>Name</c> is the model name, shared by two boards of
    /// one model, so the serial is the only term that separates them (PR #337, Qodo round 2).
    /// </summary>
    [Fact]
    public async Task The_skipped_wifi_notice_is_not_stacked_by_a_differently_cased_serial()
    {
        var coordinator = CreateCoordinator();

        await coordinator.UpdateWifiModuleOnlyAsync(WincDevice("COM-TEST-339-A", "Nq1", "sn-0001"));
        await coordinator.UpdateWifiModuleOnlyAsync(WincDevice("COM-TEST-339-B", "Nq1", "SN-0001"));

        Assert.Single(_host.Notifications);
    }

    /// <summary>
    /// The negative control: two genuinely different boards of the same model still get a notice
    /// each, which is the behaviour #337 added the serial to the match to protect.
    /// </summary>
    [Fact]
    public async Task Two_different_boards_still_each_get_a_skipped_wifi_notice()
    {
        var coordinator = CreateCoordinator();

        await coordinator.UpdateWifiModuleOnlyAsync(WincDevice("COM-TEST-339-A", "Nq1", "SN-0001"));
        await coordinator.UpdateWifiModuleOnlyAsync(WincDevice("COM-TEST-339-B", "Nq1", "SN-0002"));

        Assert.Equal(2, _host.Notifications.Count);
    }
    #endregion

    #region Helpers
    /// <summary>
    /// A board running 3.8.0, which <see cref="StubDownloadService"/> reports as outdated against
    /// the 3.9.0 release it serves.
    /// </summary>
    private static SerialStreamingDevice Device(string serial) =>
        new("COM-TEST-339", "Nq1", serial, firmwareVersion: "3.8.0");

    private static SerialStreamingDevice WincDevice(string portName, string deviceName, string serial)
    {
        var device = new SerialStreamingDevice(portName, deviceName, serial, firmwareVersion: "3.8.0");
        device.Metadata.Capabilities.HasWincWifiModule = true;
        return device;
    }
    #endregion

    #region Fakes
    /// <summary>
    /// Host seam carrying the notification list the coordinator de-duplicates against, plus a
    /// settable connected-device list — <c>RefreshFirmwareUpdatesAsync</c> reads its devices from
    /// here.
    /// </summary>
    private sealed class RecordingHost : IFirmwareUpdateHost
    {
        private List<DesktopStreamingDevice> _connectedDevices = [];

        public void SetConnectedDevices(params DesktopStreamingDevice[] devices) =>
            _connectedDevices = [.. devices];

        public DesktopStreamingDevice? SelectedDevice { get; set; }

        public IReadOnlyList<DesktopStreamingDevice> ConnectedDevices => _connectedDevices;

        public string FirmwareFilePath { get; set; } = string.Empty;

        public bool SelectedDeviceSupportsFirmwareUpdate { get; set; }

        public bool IsFirmwareUploading { get; set; }

        public bool IsUploadComplete { get; set; }

        public bool HasErrorOccured { get; set; }

        public int UploadFirmwareProgress { get; set; }

        public int UploadWiFiProgress { get; set; }

        public string FirmwareUpdateStatusText { set { } }

        public ObservableCollection<Notifications> Notifications { get; } = [];

        public DesktopStreamingDevice? DeviceBeingUpdated { get; set; }

        public bool NotificationCountRefreshed { get; private set; }

        public void RefreshNotificationCount() => NotificationCountRefreshed = true;

        public void ShowFirmwareError(string message) { }

        public void ShowFirmwareUpdateSucceeded() { }

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Download service that reports one release, newer than every device here, and no WiFi package
    /// — the cheapest clean exit from a flash that has genuinely started.
    /// </summary>
    private sealed class StubDownloadService : IFirmwareDownloadService
    {
        private static readonly FirmwareReleaseInfo LatestRelease = new()
        {
            Version = new FirmwareVersion(3, 9, 0, PreLabel: null, PreNumber: 0),
            TagName = "v3.9.0",
            IsPreRelease = false
        };

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<(string, string)?>(null);

        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FirmwareReleaseInfo?>(LatestRelease);

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FirmwareUpdateCheckResult
            {
                UpdateAvailable = true,
                LatestRelease = LatestRelease
            });

        public void InvalidateCache() { }
    }

    /// <summary>PIC32 update service that these tests never reach.</summary>
    private sealed class StubUpdateService : IFirmwareUpdateService
    {
        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

#pragma warning disable CS0067 // Part of the interface; nothing in these tests raises it.
        public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress,
            string? targetDevicePath,
            string? targetLocationKey,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateWifiModuleAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string firmwarePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool skipVersionCheck = false) => Task.CompletedTask;

        public Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    #endregion
}
