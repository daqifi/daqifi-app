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
/// the success dialog is shown — without a network request inside the write.
///
/// <para>
/// Before the fix, <see cref="FirmwareUpdateCoordinator.RefreshFirmwareUpdatesAsync"/> ran only
/// from the end of the shell's <c>UpdateUi</c>. The device stays connected for the whole update, so
/// nothing called it when the update completed, and a board on the newest firmware kept the
/// notification that told the user to update it.
/// </para>
///
/// <para>
/// The fix compares the version the device reports after the restart with the release the
/// coordinator already recorded, and refreshes the release list only after the run has closed. The
/// release lookup takes no token, so it must never hold the upload open: the tests below make it
/// hang and throw.
/// </para>
///
/// <para>
/// The device is the app's real <see cref="SerialStreamingDevice"/> over a real, connected Core
/// device. Each test first runs the real connect-time check, which is what records the release and
/// raises the notification in the app. The PIC32 flash is a stub that does what the restart does to
/// Core's metadata: it writes the version the board boots with. It does NOT touch the wrapper, which
/// is the case where the wrapper has not yet heard Core's <c>ChannelsPopulated</c>.
/// </para>
/// </summary>
public class FirmwareUpdateOutdatedNotificationTests : IDisposable
{
    private const string Serial = "7E2815916200E898";
    private const string Latest = "3.9.0";
    private const string Previous = "3.8.0";
    private const string Older = "3.7.2";

    /// <summary>A run that does not depend on the network finishes at once; a hung one fails fast.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeHost _host = new();
    private readonly ReleaseDownloadService _downloads;
    private readonly RestartingUpdateService _updates = new();
    private readonly RecordingAppLogger _logger = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-firmware-outdated-tests", Guid.NewGuid().ToString("N"));

    private readonly Daqifi.Avalonia.Tests.Device.CapturingTransport _transport = new();
    private readonly DaqifiStreamingDevice _core;
    private readonly SerialStreamingDevice _device;
    private readonly FirmwareUpdateCoordinator _coordinator;

    public FirmwareUpdateOutdatedNotificationTests()
    {
        _downloads = new ReleaseDownloadService(_host, Latest);
        _host.CountLookups = () => _downloads.LatestReleaseQueries;

        _core = new DaqifiStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _device = new SerialStreamingDevice("COM-TEST-566", _core);

        _host.SelectedDevice = _device;
        _host.ConnectedDevices.Add(_device);
        _updates.Core = _core;

        // canFlashWifiModule: false and a device with no WINC module keep the WiFi step out of these
        // tests: it returns at once for a device without the module, so only the PIC32 half, the
        // comparison and the post-run refresh run.
        _coordinator = new FirmwareUpdateCoordinator(
            _host,
            _updates,
            _downloads,
            NullLogger<FirmwareUpdateService>.Instance,
            _logger,
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: false);
    }

    public void Dispose()
    {
        _downloads.ReleaseHungLookups();

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
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;

        await RunUpdate();

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
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;

        await RunUpdate();

        Assert.Equal(0, _host.FirmwareNotificationsWhenSucceeded);
        Assert.False(_host.WasOutdatedWhenSucceeded);
    }

    /// <summary>
    /// The review finding on PR #574: the release lookup takes no token, so when GitHub never answers
    /// it must not hold the run open. The notification is settled anyway, the run completes, and the
    /// upload flag (which keeps Cancel enabled) is down.
    /// </summary>
    [Fact]
    public async Task A_release_lookup_that_never_answers_does_not_hold_the_run_open()
    {
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;
        _downloads.Lookup = LookupMode.Hang;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.False(_host.IsFirmwareUploading);
        Assert.False(_device.IsFirmwareOutdated);
        Assert.DoesNotContain(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(0, _host.NotificationCount);
    }

    /// <summary>
    /// The other half of the finding: a lookup that fails must not leave the old notification
    /// standing. The full check swallows the failure and changes nothing, so it cannot be the thing
    /// that settles the notification.
    /// </summary>
    [Fact]
    public async Task A_release_lookup_that_fails_does_not_leave_the_old_notification()
    {
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;
        _downloads.Lookup = LookupMode.Throw;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.False(_host.IsFirmwareUploading);
        Assert.False(_device.IsFirmwareOutdated);
        Assert.DoesNotContain(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(0, _host.NotificationCount);
    }

    /// <summary>
    /// The release list is refreshed separately: only after the run has closed, so it never holds
    /// <c>IsFirmwareUploading</c> up, and never before the success dialog.
    /// </summary>
    [Fact]
    public async Task The_release_list_is_refreshed_only_after_the_run_has_closed()
    {
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;
        var lookupsBefore = _downloads.LatestReleaseQueries;

        await RunUpdate();

        Assert.True(_downloads.LatestReleaseQueries > lookupsBefore, "No release refresh followed the run.");
        Assert.All(_downloads.UploadingWhenLookedUp.Skip(lookupsBefore), Assert.False);
        Assert.Equal(lookupsBefore, _host.LookupsWhenSucceeded);
    }

    /// <summary>
    /// What the comparison cannot see — a release published while the write ran — the separate
    /// refresh does: the board is on the release recorded at connect time, but a newer one now exists.
    /// </summary>
    [Fact]
    public async Task A_release_published_during_the_write_is_found_by_the_refresh()
    {
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;
        _downloads.Publish("3.10.0");

        await RunUpdate();

        // Settled against the recorded 3.9.0 when the dialog opened ...
        Assert.Equal(0, _host.FirmwareNotificationsWhenSucceeded);

        // ... and corrected by the refresh once the run had closed.
        Assert.True(_device.IsFirmwareOutdated);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
    }

    /// <summary>
    /// A manual <c>.hex</c> of the newest release clears the notification too.
    /// </summary>
    [Fact]
    public async Task A_manual_upload_of_the_latest_firmware_removes_the_outdated_notification()
    {
        await ConnectOn(Previous);
        _host.FirmwareFilePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.False(_device.IsFirmwareOutdated);
        Assert.DoesNotContain(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(0, _host.NotificationCount);
    }

    /// <summary>
    /// The other direction: a manual upload that installs an OLDER image on an up-to-date board
    /// must bring the notification back. A fix that only removed the notification would pass every
    /// removal test and fail this one. The lookup hangs, so it is the comparison that adds it,
    /// before the dialog.
    /// </summary>
    [Fact]
    public async Task A_manual_upload_of_an_older_firmware_brings_the_notification_back()
    {
        await ConnectOn(Latest);
        _host.FirmwareFilePath = FirmwareFile();
        _updates.VersionAfterRestart = Previous;
        _downloads.Lookup = LookupMode.Hang;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.Equal(1, _host.FirmwareNotificationsWhenSucceeded);
        Assert.Equal(Previous, _device.DeviceVersion);
        Assert.True(_device.IsFirmwareOutdated);
        var notification = Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(Serial, notification.DeviceSerialNo);
        Assert.Equal(1, _host.NotificationCount);
    }

    /// <summary>
    /// A manual upload that is still older than the newest release keeps the notification it
    /// already had, once — neither the comparison nor the refresh may stack a second copy.
    /// </summary>
    [Fact]
    public async Task A_manual_upload_that_is_still_outdated_keeps_one_notification()
    {
        await ConnectOn(Older);
        _host.FirmwareFilePath = FirmwareFile();
        _updates.VersionAfterRestart = Previous;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.True(_device.IsFirmwareOutdated);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(1, _host.NotificationCount);
    }

    /// <summary>
    /// When Core has no version after the reset (the board never answered), nothing is guessed:
    /// the wrapper keeps the version it had and the notification is left as it was.
    /// </summary>
    [Fact]
    public async Task An_empty_version_after_the_restart_changes_nothing()
    {
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = string.Empty;
        _downloads.Lookup = LookupMode.Hang;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.Equal(Previous, _device.DeviceVersion);
        Assert.True(_device.IsFirmwareOutdated);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
    }

    /// <summary>
    /// When no release was ever recorded (the connect-time check never succeeded), there is nothing
    /// to compare with, so the notification is not touched before the dialog.
    /// </summary>
    [Fact]
    public async Task No_recorded_release_changes_nothing_before_the_dialog()
    {
        // No ConnectOn: the coordinator has recorded no release. A notification is standing anyway,
        // as one would after a check whose result has since been cleared.
        _device.DeviceSerialNo = Serial;
        _device.DeviceVersion = Previous;
        _core.Metadata.FirmwareVersion = Previous;
        _device.IsFirmwareOutdated = true;
        _host.Notifications.Add(new Notifications
        {
            DeviceSerialNo = Serial,
            Message = $"Device With Serial {Serial} has Outdated Firmware. Please Update to Version {Latest}.",
            IsFirmwareUpdate = true
        });
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;
        _downloads.Lookup = LookupMode.Hang;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.Equal(1, _host.FirmwareNotificationsWhenSucceeded);
        Assert.True(_host.WasOutdatedWhenSucceeded);
    }

    /// <summary>
    /// A failed write changes nothing the check reads: the notification stays, and no refresh runs.
    /// </summary>
    [Fact]
    public async Task A_failed_write_changes_nothing_and_does_not_refresh()
    {
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _updates.VersionAfterRestart = Latest;
        _updates.FailFlash = true;
        var lookupsBefore = _downloads.LatestReleaseQueries;

        await RunUpdate();

        Assert.False(_host.UpdateSucceeded);
        Assert.Equal(lookupsBefore, _downloads.LatestReleaseQueries);
        Assert.True(_device.IsFirmwareOutdated);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
    }

    #region Helpers
    /// <summary>
    /// Connects the device on <paramref name="version"/> and runs the real connect-time check, which
    /// records the latest release and raises or clears the notification, as the app does.
    /// </summary>
    private async Task ConnectOn(string version)
    {
        _device.DeviceSerialNo = Serial;
        _device.DeviceVersion = version;
        _core.Metadata.FirmwareVersion = version;

        await _coordinator.RefreshFirmwareUpdatesAsync();

        Assert.Equal(Latest, _coordinator.LatestFirmwareVersion);
    }

    /// <summary>Runs the update, failing the test rather than hanging if the run never completes.</summary>
    private Task RunUpdate() => _coordinator.UploadFirmwareAsync().WaitAsync(RunTimeout);

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
    private enum LookupMode
    {
        Answer,
        Hang,
        Throw
    }

    /// <summary>
    /// Download service with one published release. <see cref="CheckForUpdateAsync"/> follows Core's
    /// rule: an update is available when the release is newer than the device. The release lookup
    /// can be made to answer, hang until teardown, or throw, and it records whether the host was
    /// uploading each time it was asked.
    /// </summary>
    private sealed class ReleaseDownloadService(FakeHost host, string latestVersion) : IFirmwareDownloadService
    {
        private readonly CancellationTokenSource _releaseHung = new();
        private FirmwareReleaseInfo _latest = Release(latestVersion);

        /// <summary>What the PIC32 package download returns; null means "no package".</summary>
        public string? LatestFirmwarePath { get; set; }

        public LookupMode Lookup { get; set; } = LookupMode.Answer;

        /// <summary>How many times the release lookup was asked.</summary>
        public int LatestReleaseQueries { get; private set; }

        /// <summary>The host's upload flag at each lookup, in order.</summary>
        public List<bool> UploadingWhenLookedUp { get; } = [];

        /// <summary>Makes a newer release the latest, as a release published mid-write would.</summary>
        public void Publish(string version) => _latest = Release(version);

        /// <summary>Frees any hung lookup so it cannot outlive the test.</summary>
        public void ReleaseHungLookups() => _releaseHung.Cancel();

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

        public async Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default)
        {
            LatestReleaseQueries++;
            UploadingWhenLookedUp.Add(host.IsFirmwareUploading);

            switch (Lookup)
            {
                case LookupMode.Hang:
                    await Task.Delay(Timeout.InfiniteTimeSpan, _releaseHung.Token);
                    return null;
                case LookupMode.Throw:
                    throw new HttpRequestException("Simulated: GitHub is unavailable.");
                default:
                    return _latest;
            }
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
                UpdateAvailable = !parsed || _latest.Version > deviceVersion,
                DeviceVersion = parsed ? deviceVersion : null,
                LatestRelease = _latest
            });
        }

        public void InvalidateCache() { }

        private static FirmwareReleaseInfo Release(string version) => new()
        {
            Version = FirmwareVersion.TryParse(version, out var parsed)
                ? parsed
                : throw new ArgumentException($"Not a firmware version: {version}", nameof(version)),
            TagName = "v" + version,
            IsPreRelease = false
        };
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

        /// <summary>Set by the test's download service so the dialog can record how many lookups preceded it.</summary>
        public Func<int>? CountLookups { get; set; }

        public bool UpdateSucceeded { get; private set; }

        public int FirmwareNotificationsWhenSucceeded { get; private set; } = -1;

        public bool WasOutdatedWhenSucceeded { get; private set; }

        public int LookupsWhenSucceeded { get; private set; } = -1;

        public void RefreshNotificationCount() => NotificationCount = Notifications.Count;

        public void ShowFirmwareError(string message) { }

        public void ShowFirmwareUpdateSucceeded()
        {
            UpdateSucceeded = true;
            FirmwareNotificationsWhenSucceeded = Notifications.Count(n => n.IsFirmwareUpdate);
            WasOutdatedWhenSucceeded = SelectedDevice?.IsFirmwareOutdated ?? false;
            LookupsWhenSucceeded = CountLookups?.Invoke() ?? -1;
        }

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
    #endregion
}
