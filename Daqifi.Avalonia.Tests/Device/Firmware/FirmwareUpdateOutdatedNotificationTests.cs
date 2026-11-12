using System.Collections.ObjectModel;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DesktopStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins issue #566: after a successful firmware write, the "has Outdated Firmware" notification
/// and the device's outdated flag describe the firmware the device now runs, and they do so before
/// the success dialog is shown.
///
/// <para>
/// Before the fix, <see cref="FirmwareUpdateCoordinator.RefreshFirmwareUpdatesAsync"/> ran only
/// from the end of the shell's <c>UpdateUi</c>. The device stays connected for the whole update, so
/// nothing called it when the update completed, and a board on the newest firmware kept the
/// notification that told the user to update it.
/// </para>
///
/// <para>
/// The device is the app's real <see cref="SerialStreamingDevice"/> over a real, connected Core
/// device. The PIC32 flash is a stub that does what the restart does to Core's metadata: it writes
/// the version the board boots with. It does NOT touch the wrapper, which is the case where the
/// wrapper has not yet heard Core's <c>ChannelsPopulated</c> — the check must still read the new
/// version.
/// </para>
/// </summary>
public class FirmwareUpdateOutdatedNotificationTests : IDisposable
{
    private const string Serial = "7E2815916200E898";
    private const string Latest = "3.9.0";
    private const string Previous = "3.8.0";
    private const string Older = "3.7.2";

    private readonly FakeHost _host = new();
    private readonly ReleaseDownloadService _downloads = new(Latest);
    private readonly RestartingUpdateService _updates = new();
    private readonly RecordingAppLogger _logger = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-firmware-outdated-tests", Guid.NewGuid().ToString("N"));

    private readonly Daqifi.Avalonia.Tests.Device.CapturingTransport _transport = new();
    private readonly DaqifiStreamingDevice _core;
    private readonly SerialStreamingDevice _device;

    public FirmwareUpdateOutdatedNotificationTests()
    {
        _core = new DaqifiStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _device = new SerialStreamingDevice("COM-TEST-566", _core);

        _host.SelectedDevice = _device;
        _host.ConnectedDevices.Add(_device);
        _updates.Core = _core;
    }

    public void Dispose()
    {
        // Release the parked reader first, as CurrentRateCapEnforcementTests does, so Core's
        // teardown does not spend its consumer-join bound on every run.
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();

        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The regression in the issue: an update to the newest release removes the notification, and
    /// the count the bell shows agrees with the list.
    /// </summary>
    [Fact]
    public async Task An_update_to_the_latest_firmware_removes_the_outdated_notification()
    {
        StartOn(Previous, withNotification: true);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;

        await CreateCoordinator().UploadFirmwareAsync();

        Assert.True(_host.UpdateSucceeded, "The update did not reach the success dialog.");
        Assert.Equal(Latest, _device.DeviceVersion);
        Assert.False(_device.IsFirmwareOutdated);
        Assert.DoesNotContain(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(0, _host.NotificationCount);
    }

    /// <summary>
    /// "Before the success dialog", not merely "eventually": the user reads the bell while the
    /// dialog is open, and the shell's own <c>UpdateUi</c> is not coming to correct it.
    /// </summary>
    [Fact]
    public async Task The_notification_is_gone_before_the_success_dialog_shows()
    {
        StartOn(Previous, withNotification: true);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;

        await CreateCoordinator().UploadFirmwareAsync();

        Assert.Equal(0, _host.FirmwareNotificationsWhenSucceeded);
        Assert.False(_host.WasOutdatedWhenSucceeded);
    }

    /// <summary>
    /// A manual <c>.hex</c> of the newest release clears the notification too.
    /// </summary>
    [Fact]
    public async Task A_manual_upload_of_the_latest_firmware_removes_the_outdated_notification()
    {
        StartOn(Previous, withNotification: true);
        _host.FirmwareFilePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;

        await CreateCoordinator().UploadFirmwareAsync();

        Assert.True(_host.UpdateSucceeded);
        Assert.False(_device.IsFirmwareOutdated);
        Assert.DoesNotContain(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(0, _host.NotificationCount);
    }

    /// <summary>
    /// The other direction: a manual upload that installs an OLDER image on an up-to-date board
    /// must bring the notification back. A fix that only removed the notification would pass every
    /// test above and fail this one.
    /// </summary>
    [Fact]
    public async Task A_manual_upload_of_an_older_firmware_brings_the_notification_back()
    {
        StartOn(Latest, withNotification: false);
        _host.FirmwareFilePath = FirmwareFile();
        _updates.VersionAfterRestart = Previous;

        await CreateCoordinator().UploadFirmwareAsync();

        Assert.True(_host.UpdateSucceeded);
        Assert.Equal(Previous, _device.DeviceVersion);
        Assert.True(_device.IsFirmwareOutdated);
        var notification = Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(Serial, notification.DeviceSerialNo);
        Assert.Equal(1, _host.NotificationCount);
    }

    /// <summary>
    /// A manual upload that is still older than the newest release keeps the notification it
    /// already had, once — the re-check must not stack a second copy.
    /// </summary>
    [Fact]
    public async Task A_manual_upload_that_is_still_outdated_keeps_one_notification()
    {
        StartOn(Older, withNotification: true);
        _host.FirmwareFilePath = FirmwareFile();
        _updates.VersionAfterRestart = Previous;

        await CreateCoordinator().UploadFirmwareAsync();

        Assert.True(_host.UpdateSucceeded);
        Assert.True(_device.IsFirmwareOutdated);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(1, _host.NotificationCount);
    }

    /// <summary>
    /// When Core has no version after the reset (the board never answered), the wrapper keeps the
    /// version it had rather than being blanked, and the notification is left as it was.
    /// </summary>
    [Fact]
    public async Task An_empty_version_after_the_restart_does_not_erase_the_device_version()
    {
        StartOn(Previous, withNotification: true);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = string.Empty;

        await CreateCoordinator().UploadFirmwareAsync();

        Assert.True(_host.UpdateSucceeded);
        Assert.Equal(Previous, _device.DeviceVersion);
        Assert.True(_device.IsFirmwareOutdated);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
    }

    /// <summary>
    /// The re-check belongs to the success path only. A failed write changes nothing the check
    /// reads, so it must not run, and the notification stays.
    /// </summary>
    [Fact]
    public async Task A_failed_write_does_not_run_the_check()
    {
        StartOn(Previous, withNotification: true);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;
        _updates.FailFlash = true;

        await CreateCoordinator().UploadFirmwareAsync();

        Assert.False(_host.UpdateSucceeded);
        Assert.Equal(0, _downloads.LatestReleaseQueries);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
    }

    #region Helpers
    /// <summary>
    /// <c>canFlashWifiModule: false</c> and a device with no WINC module keep the WiFi step out of
    /// these tests: it returns at once for a device without the module, so only the PIC32 half and
    /// the re-check run.
    /// </summary>
    private FirmwareUpdateCoordinator CreateCoordinator() =>
        new(_host,
            _updates,
            _downloads,
            NullLogger<FirmwareUpdateService>.Instance,
            _logger,
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: false);

    /// <summary>
    /// Puts the device, Core and the host in the state the connect-time check leaves them in.
    /// </summary>
    private void StartOn(string version, bool withNotification)
    {
        _device.DeviceSerialNo = Serial;
        _device.DeviceVersion = version;
        _core.Metadata.FirmwareVersion = version;
        _device.IsFirmwareOutdated = withNotification;

        if (withNotification)
        {
            _host.Notifications.Add(new Notifications
            {
                DeviceSerialNo = Serial,
                Message = $"Device With Serial {Serial} has Outdated Firmware. Please Update to Version {Latest}.",
                IsFirmwareUpdate = true
            });
        }

        _host.NotificationCount = _host.Notifications.Count;
    }

    /// <summary>A stand-in PIC32 image: the coordinator checks only that the file exists.</summary>
    private string FirmwareFile()
    {
        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, "firmware.hex");
        File.WriteAllText(path, ":00000001FF\n");
        return path;
    }
    #endregion

    #region Fakes
    /// <summary>
    /// Download service with one published release. <see cref="CheckForUpdateAsync"/> compares
    /// the way Core's does: an update is available when the release is newer than the device.
    /// </summary>
    private sealed class ReleaseDownloadService(string latestVersion) : IFirmwareDownloadService
    {
        private readonly FirmwareReleaseInfo _latest = new()
        {
            Version = ParseVersion(latestVersion),
            TagName = "v" + latestVersion,
            IsPreRelease = false
        };

        /// <summary>What the PIC32 package download returns; null means "no package".</summary>
        public string? LatestFirmwarePath { get; set; }

        /// <summary>How many times the check asked for the latest release.</summary>
        public int LatestReleaseQueries { get; private set; }

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<(string ExtractedPath, string Version)?>(null);

        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(LatestFirmwarePath);

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default)
        {
            LatestReleaseQueries++;
            return Task.FromResult<FirmwareReleaseInfo?>(_latest);
        }

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default)
        {
            var parsed = FirmwareVersion.TryParse(deviceVersionString, out var deviceVersion);
            return Task.FromResult(new FirmwareUpdateCheckResult
            {
                UpdateAvailable = parsed && _latest.Version > deviceVersion,
                DeviceVersion = parsed ? deviceVersion : null,
                LatestRelease = _latest
            });
        }

        public void InvalidateCache() { }

        private static FirmwareVersion ParseVersion(string value) =>
            FirmwareVersion.TryParse(value, out var version)
                ? version
                : throw new ArgumentException($"Not a firmware version: {value}", nameof(value));
    }

    /// <summary>
    /// PIC32 update service that flashes nothing. In place of the flash it does what the restart
    /// does to Core's metadata — the board comes back reporting <see cref="VersionAfterRestart"/>
    /// — or throws, for the failure path.
    /// </summary>
    private sealed class RestartingUpdateService : IFirmwareUpdateService
    {
        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

        public DaqifiStreamingDevice? Core { get; set; }

        public string VersionAfterRestart { get; set; } = string.Empty;

        public bool FailFlash { get; set; }

#pragma warning disable CS0067 // Part of the interface; nothing in these tests raises it.
        public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The coordinator uses the targeted overload.");

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress,
            string? targetDevicePath,
            string? targetLocationKey,
            CancellationToken cancellationToken = default)
        {
            if (FailFlash)
            {
                throw new InvalidOperationException("Simulated flash failure.");
            }

            Core!.Metadata.FirmwareVersion = VersionAfterRestart;
            return Task.CompletedTask;
        }

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

    /// <summary>
    /// Host seam. <see cref="RefreshNotificationCount"/> does what the shell's does
    /// (<c>NotificationCount = NotificationList.Count</c>), and the success dialog records what the
    /// bell and the tile would show at the moment it opens.
    /// </summary>
    private sealed class FakeHost : IFirmwareUpdateHost
    {
        public DesktopStreamingDevice? SelectedDevice { get; set; }

        public List<DesktopStreamingDevice> ConnectedDevices { get; } = [];

        IReadOnlyList<DesktopStreamingDevice> IFirmwareUpdateHost.ConnectedDevices => ConnectedDevices;

        public string FirmwareFilePath { get; set; } = string.Empty;

        public bool SelectedDeviceSupportsFirmwareUpdate { get; set; }

        public bool IsFirmwareUploading { get; set; }

        public bool IsUploadComplete { get; set; }

        public bool HasErrorOccured { get; set; }

        public int UploadFirmwareProgress { get; set; }

        public int UploadWiFiProgress { get; set; }

        public string FirmwareUpdateStatusText { get; set; } = string.Empty;

        public ObservableCollection<Notifications> Notifications { get; } = [];

        public DesktopStreamingDevice? DeviceBeingUpdated { get; set; }

        public int NotificationCount { get; set; }

        public bool UpdateSucceeded { get; private set; }

        public int FirmwareNotificationsWhenSucceeded { get; private set; } = -1;

        public bool WasOutdatedWhenSucceeded { get; private set; }

        public void RefreshNotificationCount() => NotificationCount = Notifications.Count;

        public void ShowFirmwareError(string message) { }

        public void ShowFirmwareUpdateSucceeded()
        {
            UpdateSucceeded = true;
            FirmwareNotificationsWhenSucceeded = Notifications.Count(n => n.IsFirmwareUpdate);
            WasOutdatedWhenSucceeded = SelectedDevice?.IsFirmwareOutdated ?? false;
        }

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
    #endregion
}
