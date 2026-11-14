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
/// The device's version after the restart comes from a fresh status reply of the restarted device
/// (<see cref="PostRestartFirmwareVersion"/>), not from Core's stored <c>Metadata.FirmwareVersion</c>,
/// which Core keeps when a reply omits the version. Most tests set that reading through the
/// coordinator's <c>readPostRestartVersion</c> seam; the stub flash leaves Core's metadata at the
/// OLD version unless a test says otherwise, so every removal test also shows the stored value is
/// not what is read. Two tests use the real reader against a Core device that answers the status
/// request (<see cref="StatusReplyingCoreDevice"/>).
/// </para>
///
/// <para>
/// The device is the app's real <see cref="SerialStreamingDevice"/> over a real, connected Core
/// device. Each test first runs the real connect-time check, which is what records the release and
/// raises the notification in the app.
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
    private readonly ReportedVersionReader _reader = new();
    private readonly RecordingAppLogger _logger = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-firmware-outdated-tests", Guid.NewGuid().ToString("N"));

    private readonly Daqifi.Avalonia.Tests.Device.CapturingTransport _transport = new();
    private readonly StatusReplyingCoreDevice _core;
    private readonly SerialStreamingDevice _device;
    private readonly FirmwareUpdateCoordinator _coordinator;

    public FirmwareUpdateOutdatedNotificationTests()
    {
        _downloads = new ReleaseDownloadService(_host, Latest);
        _host.CountLookups = () => _downloads.LatestReleaseQueries;
        _downloads.WriteHasStarted = () => _updates.FlashesStarted > 0;

        _core = new StatusReplyingCoreDevice(_transport);
        _core.Connect();
        _device = new SerialStreamingDevice("COM-TEST-566", _core);

        _host.SelectedDevice = _device;
        _host.ConnectedDevices.Add(_device);
        _updates.Core = _core;

        _coordinator = CreateCoordinator(_reader.ReadAsync);
    }

    /// <summary>
    /// <c>canFlashWifiModule: false</c> and a device with no WINC module keep the WiFi step out of
    /// these tests: it returns at once for a device without the module, so only the PIC32 half, the
    /// version read, the comparison and the post-run refresh run. A null reader is the production
    /// default, <see cref="PostRestartFirmwareVersion.ReadAsync"/>.
    /// </summary>
    private FirmwareUpdateCoordinator CreateCoordinator(
        Func<DaqifiDevice, CancellationToken, Task<string?>>? readPostRestartVersion,
        bool canFlashWifiModule = false) =>
        new(_host,
            _updates,
            _downloads,
            NullLogger<FirmwareUpdateService>.Instance,
            _logger,
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: canFlashWifiModule,
            readPostRestartVersion: readPostRestartVersion);

    public void Dispose()
    {
        _downloads.ReleaseHungLookups();
        _reader.ReleaseHungReads();
        _core.StopAnswering();

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
    /// the count the bell shows agrees with the list. The write goes through the confirmation that
    /// issue #564 added and downloads the confirmed release by its tag.
    /// </summary>
    [Fact]
    public async Task An_update_to_the_latest_firmware_removes_the_outdated_notification()
    {
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _reader.Reported = Latest;

        await RunUpdate();

        Assert.Equal(1, _host.Confirmations);
        Assert.Equal(["v" + Latest], _downloads.TagsDownloaded);
        Assert.True(_host.UpdateSucceeded, "The update did not reach the success dialog.");
        Assert.Equal(Latest, _device.DeviceVersion);
        Assert.False(_device.IsFirmwareOutdated);
        Assert.DoesNotContain(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(_host.Notifications.Count, _host.NotificationCount);
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
        _reader.Reported = Latest;

        await RunUpdate();

        Assert.Equal(0, _host.FirmwareNotificationsWhenSucceeded);
        Assert.False(_host.WasOutdatedWhenSucceeded);
    }

    /// <summary>
    /// The review finding on PR #574: the release lookup takes no token, so when GitHub never answers
    /// it must not hold the run open. The notification is settled anyway, the run completes, and the
    /// upload flag (which keeps Cancel enabled) is down. (The lookup that names the release in the
    /// confirmation, before the run, still answers; GitHub stops answering once the write starts.)
    /// </summary>
    [Fact]
    public async Task A_release_lookup_that_never_answers_does_not_hold_the_run_open()
    {
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _reader.Reported = Latest;
        _downloads.Lookup = LookupMode.Hang;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.False(_host.IsFirmwareUploading);
        Assert.False(_device.IsFirmwareOutdated);
        Assert.DoesNotContain(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(_host.Notifications.Count, _host.NotificationCount);
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
        _reader.Reported = Latest;
        _downloads.Lookup = LookupMode.Throw;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.False(_host.IsFirmwareUploading);
        Assert.False(_device.IsFirmwareOutdated);
        Assert.DoesNotContain(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(_host.Notifications.Count, _host.NotificationCount);
    }

    /// <summary>
    /// The release list is refreshed separately: only after the run has closed, so it never holds
    /// <c>IsFirmwareUploading</c> up, and never before the success dialog. The one lookup before the
    /// dialog is the one that names the release in the confirmation (issue #564), and it runs before
    /// the run opens — so no lookup at all happens while the host is uploading.
    /// </summary>
    [Fact]
    public async Task The_release_list_is_refreshed_only_after_the_run_has_closed()
    {
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _reader.Reported = Latest;
        var lookupsBefore = _downloads.LatestReleaseQueries;

        await RunUpdate();

        Assert.Equal(lookupsBefore + 1, _host.LookupsWhenSucceeded);
        Assert.True(_downloads.LatestReleaseQueries > _host.LookupsWhenSucceeded, "No release refresh followed the run.");
        Assert.All(_downloads.UploadingWhenLookedUp, Assert.False);
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
        _reader.Reported = Latest;
        _updates.OnFlash = () => _downloads.Publish("3.10.0");

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
        _reader.Reported = Latest;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.False(_device.IsFirmwareOutdated);
        Assert.DoesNotContain(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(_host.Notifications.Count, _host.NotificationCount);
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
        _reader.Reported = Previous;
        _downloads.Lookup = LookupMode.Hang;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.Equal(1, _host.FirmwareNotificationsWhenSucceeded);
        Assert.Equal(Previous, _device.DeviceVersion);
        Assert.True(_device.IsFirmwareOutdated);
        var notification = Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(Serial, notification.DeviceSerialNo);
        Assert.Equal(_host.Notifications.Count, _host.NotificationCount);
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
        _reader.Reported = Previous;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.True(_device.IsFirmwareOutdated);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(_host.Notifications.Count, _host.NotificationCount);
    }

    /// <summary>
    /// The review finding on head <c>2a59dcb</c>: the version must come from the restarted device.
    /// Core's stored metadata still says 3.8.0 (a reply without a version, or an initialization that
    /// failed after the reconnect, leaves it there); the fresh reply says 3.9.0. The fresh reply wins.
    /// </summary>
    [Fact]
    public async Task A_fresh_reply_is_used_while_metadata_still_holds_the_old_version()
    {
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _reader.Reported = Latest;
        _downloads.Lookup = LookupMode.Hang;

        await RunUpdate();

        Assert.Equal(Previous, _core.Metadata.FirmwareVersion);
        Assert.Equal(1, _reader.Reads);
        Assert.Equal(Latest, _device.DeviceVersion);
        Assert.False(_device.IsFirmwareOutdated);
        Assert.Equal(0, _host.FirmwareNotificationsWhenSucceeded);
        Assert.Equal(_host.Notifications.Count, _host.NotificationCount);
    }

    /// <summary>
    /// "Unknown" stays unknown: when the restarted device's version is not known, nothing changes —
    /// even though Core's metadata holds the newest version, which a guess would have trusted. The
    /// refresh after the run reads the wrapper's still-old version and keeps the notification too.
    /// </summary>
    [Fact]
    public async Task An_unknown_version_changes_nothing_even_when_metadata_has_the_new_one()
    {
        await ConnectOn(Previous);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _reader.Reported = null;
        _updates.MetadataAfterRestart = Latest;

        await RunUpdate();

        Assert.True(_host.UpdateSucceeded);
        Assert.Equal(Latest, _core.Metadata.FirmwareVersion);
        Assert.Equal(1, _host.FirmwareNotificationsWhenSucceeded);
        Assert.True(_host.WasOutdatedWhenSucceeded);

        // After the refresh that followed the run, too.
        Assert.Equal(Previous, _device.DeviceVersion);
        Assert.True(_device.IsFirmwareOutdated);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
        Assert.Equal(_host.Notifications.Count, _host.NotificationCount);
    }

    /// <summary>
    /// Review finding on head <c>abcaccb</c>: once the write has completed, a Cancel during the
    /// version read must not report the run as canceled — the device is already written, and a
    /// "canceled" result invites the user to write it again. The read still runs on the run's token
    /// (so Cancel ends the wait at once, and the reader throws), but the coordinator treats that as
    /// "version unknown" and reports the completed manual write. Unknown changes nothing.
    /// </summary>
    [Fact]
    public async Task A_cancel_during_the_version_read_after_a_manual_write_reports_the_completed_write()
    {
        await ConnectOn(Previous);
        _host.FirmwareFilePath = FirmwareFile();
        _reader.HangUntilCancelled = true;
        _downloads.Lookup = LookupMode.Hang;

        var run = _coordinator.UploadFirmwareAsync();
        await _reader.WaitUntilReadingAsync(RunTimeout);
        _coordinator.CancelUpload();
        await run.WaitAsync(RunTimeout);

        Assert.True(_reader.ReadWasCancelled, "The read did not observe the Cancel.");
        Assert.True(_host.UpdateSucceeded, "The completed write was not reported.");
        Assert.True(_host.IsUploadComplete);
        Assert.False(_host.HasErrorOccured);
        Assert.NotEqual("Firmware update canceled.", _host.FirmwareUpdateStatusText);
        Assert.False(_host.IsFirmwareUploading);

        // Unknown version: the notice and the flag are as they were.
        Assert.Equal(Previous, _device.DeviceVersion);
        Assert.True(_device.IsFirmwareOutdated);
        Assert.Equal(1, _host.FirmwareNotificationsWhenSucceeded);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
    }

    /// <summary>
    /// The other half of the finding: cancellation is kept for every update step that has yet to
    /// run. On the automatic path the WiFi-module step follows the read on the same token, so a
    /// Cancel during the read still stops it — no WiFi package is downloaded and no WiFi flash
    /// starts — and the run ends as it did before this PR for a Cancel in that step: "Firmware update
    /// canceled.", no success dialog.
    /// <para>
    /// The step has work to do here: this machine can flash the module and the device carries one.
    /// Its version is never read, because the cancelled token stops the step's version probe before
    /// anything is sent — which is the point.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_cancel_during_the_version_read_still_stops_the_wifi_step()
    {
        var coordinator = CreateCoordinator(_reader.ReadAsync, canFlashWifiModule: true);
        _device.Metadata.Capabilities.HasWincWifiModule = true;
        await ConnectOn(Previous, coordinator);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _reader.HangUntilCancelled = true;

        var run = coordinator.UploadFirmwareAsync();
        await _reader.WaitUntilReadingAsync(RunTimeout);
        coordinator.CancelUpload();
        await run.WaitAsync(RunTimeout);

        Assert.True(_reader.ReadWasCancelled);
        Assert.Equal(0, _downloads.WifiDownloads);
        Assert.Equal(0, _updates.WifiFlashes);
        Assert.Equal("Firmware update canceled.", _host.FirmwareUpdateStatusText);
        Assert.False(_host.UpdateSucceeded);
        Assert.False(_host.IsUploadComplete);
        Assert.False(_host.IsFirmwareUploading);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
    }

    /// <summary>
    /// End to end with the production reader: the restarted device answers the status request with
    /// 3.9.0 while Core's metadata still says 3.8.0. Nothing in the coordinator is faked but the flash.
    /// </summary>
    [Fact]
    public async Task With_the_device_reader_a_reply_with_the_new_version_removes_the_notice()
    {
        var coordinator = CreateCoordinator(readPostRestartVersion: null);
        await ConnectOn(Previous, coordinator);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _downloads.Lookup = LookupMode.Hang;
        var responder = _core.AnswerNextStatusRequestWith(
            new DaqifiOutMessage { DeviceFwRev = Latest });

        await coordinator.UploadFirmwareAsync().WaitAsync(RunTimeout);

        await responder.WaitAsync(RunTimeout);
        Assert.Equal(1, _core.RepliesDelivered);
        Assert.False(_device.IsFirmwareOutdated);
        Assert.Equal(0, _host.FirmwareNotificationsWhenSucceeded);
    }

    /// <summary>
    /// End to end with the production reader, the review's own case: the reply has no version, and
    /// Core's metadata holds a value (here the newest). The result is unknown, so the notice stays.
    /// </summary>
    [Fact]
    public async Task With_the_device_reader_a_reply_without_a_version_keeps_the_notice()
    {
        var coordinator = CreateCoordinator(readPostRestartVersion: null);
        await ConnectOn(Previous, coordinator);
        _downloads.LatestFirmwarePath = FirmwareFile();
        _downloads.Lookup = LookupMode.Hang;
        _updates.MetadataAfterRestart = Latest;
        var responder = _core.AnswerNextStatusRequestWith(
            new DaqifiOutMessage { DevicePn = "Nq1" });

        await coordinator.UploadFirmwareAsync().WaitAsync(RunTimeout);

        await responder.WaitAsync(RunTimeout);
        Assert.Equal(1, _core.RepliesDelivered);
        Assert.Equal(Latest, _core.Metadata.FirmwareVersion);
        Assert.True(_device.IsFirmwareOutdated);
        Assert.Equal(1, _host.FirmwareNotificationsWhenSucceeded);
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
        _reader.Reported = Latest;
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
        _reader.Reported = Latest;
        _updates.FailFlash = true;

        await RunUpdate();

        Assert.False(_host.UpdateSucceeded);
        Assert.Equal(1, _updates.FlashesStarted);
        Assert.Equal(0, _downloads.LookupsAfterWriteStarted);
        Assert.True(_device.IsFirmwareOutdated);
        Assert.Single(_host.Notifications, n => n.IsFirmwareUpdate);
    }

    #region Helpers
    /// <summary>
    /// Connects the device on <paramref name="version"/> and runs the real connect-time check, which
    /// records the latest release and raises or clears the notification, as the app does.
    /// </summary>
    private async Task ConnectOn(string version, FirmwareUpdateCoordinator? coordinator = null)
    {
        coordinator ??= _coordinator;
        _device.DeviceSerialNo = Serial;
        _device.DeviceVersion = version;
        _core.Metadata.FirmwareVersion = version;

        await coordinator.RefreshFirmwareUpdatesAsync();

        Assert.Equal(Latest, coordinator.LatestFirmwareVersion);
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
    /// <para>
    /// <see cref="Lookup"/> applies only once the write has started (<see cref="WriteHasStarted"/>).
    /// Before that, the lookup always answers: the one that names the release in the confirmation
    /// (issue #564) must succeed, or the coordinator correctly starts no write at all. The case these
    /// tests model is GitHub becoming unavailable during or after the write.
    /// </para>
    /// </summary>
    private sealed class ReleaseDownloadService(FakeHost host, string latestVersion) : IFirmwareDownloadService
    {
        private readonly CancellationTokenSource _releaseHung = new();
        private FirmwareReleaseInfo _latest = Release(latestVersion);

        /// <summary>What the PIC32 package downloads return; null means "no package".</summary>
        public string? LatestFirmwarePath { get; set; }

        public LookupMode Lookup { get; set; } = LookupMode.Answer;

        /// <summary>Whether the firmware write has started; set by the test.</summary>
        public Func<bool> WriteHasStarted { get; set; } = () => false;

        /// <summary>How many times the release lookup was asked.</summary>
        public int LatestReleaseQueries { get; private set; }

        /// <summary>How many release lookups were asked once the write had started.</summary>
        public int LookupsAfterWriteStarted { get; private set; }

        /// <summary>The tags the run downloaded, in order (issue #564: the auto path downloads by tag).</summary>
        public List<string> TagsDownloaded { get; } = [];

        /// <summary>The host's upload flag at each lookup, in order.</summary>
        public List<bool> UploadingWhenLookedUp { get; } = [];

        /// <summary>Makes a newer release the latest, as a release published mid-write would.</summary>
        public void Publish(string version) => _latest = Release(version);

        /// <summary>Frees any hung lookup so it cannot outlive the test.</summary>
        public void ReleaseHungLookups() => _releaseHung.Cancel();

        /// <summary>How many times the WiFi package was asked for.</summary>
        public int WifiDownloads { get; private set; }

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            WifiDownloads++;
            return Task.FromResult<(string ExtractedPath, string Version)?>(null);
        }

        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(LatestFirmwarePath);

        /// <summary>
        /// The auto-update downloads the release it named in the confirmation, by tag (issue #564),
        /// so this returns the same package as the "latest" download.
        /// </summary>
        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            TagsDownloaded.Add(tagName);
            return Task.FromResult(LatestFirmwarePath);
        }

        public async Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default)
        {
            LatestReleaseQueries++;
            UploadingWhenLookedUp.Add(host.IsFirmwareUploading);

            if (!WriteHasStarted())
            {
                return _latest;
            }

            LookupsAfterWriteStarted++;
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
    /// The coordinator's <c>readPostRestartVersion</c> seam: returns <see cref="Reported"/> (null is
    /// "not known"), or waits until the run's token is cancelled.
    /// </summary>
    private sealed class ReportedVersionReader
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _release = new();

        public string? Reported { get; set; }

        public bool HangUntilCancelled { get; set; }

        /// <summary>True once a read observed the run's token being cancelled and threw.</summary>
        public bool ReadWasCancelled { get; private set; }

        public int Reads { get; private set; }

        public Task WaitUntilReadingAsync(TimeSpan timeout) => _reading.Task.WaitAsync(timeout);

        public void ReleaseHungReads() => _release.Cancel();

        public async Task<string?> ReadAsync(DaqifiDevice device, CancellationToken cancellationToken)
        {
            Reads++;
            _reading.TrySetResult();

            if (HangUntilCancelled)
            {
                // Throws on cancellation, as PostRestartFirmwareVersion.ReadAsync does.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _release.Token);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ReadWasCancelled = true;
                    throw;
                }
            }

            return Reported;
        }
    }

    /// <summary>
    /// PIC32 update service that flashes nothing. By default it leaves Core's metadata as it was —
    /// the case where nothing after the reconnect updated it. <see cref="MetadataAfterRestart"/>
    /// overwrites it instead. It throws, for the failure path.
    /// </summary>
    private sealed class RestartingUpdateService : IFirmwareUpdateService
    {
        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

        public DaqifiStreamingDevice? Core { get; set; }

        /// <summary>What the flash leaves in Core's metadata; null leaves it unchanged.</summary>
        public string? MetadataAfterRestart { get; set; }

        public bool FailFlash { get; set; }

        /// <summary>How many PIC32 writes were started.</summary>
        public int FlashesStarted { get; private set; }

        /// <summary>Runs during the write, for a test that changes the world while it runs.</summary>
        public Action? OnFlash { get; set; }

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
            FlashesStarted++;
            OnFlash?.Invoke();

            if (FailFlash)
            {
                throw new InvalidOperationException("Simulated flash failure.");
            }

            if (MetadataAfterRestart != null)
            {
                Core!.Metadata.FirmwareVersion = MetadataAfterRestart;
            }

            return Task.CompletedTask;
        }

        /// <summary>How many WiFi-module flashes were started.</summary>
        public int WifiFlashes { get; private set; }

        public Task UpdateWifiModuleAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string firmwarePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool skipVersionCheck = false)
        {
            WifiFlashes++;
            return Task.CompletedTask;
        }

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

        public void ShowFirmwareUpdateSucceeded(string message)
        {
            UpdateSucceeded = true;
            FirmwareNotificationsWhenSucceeded = Notifications.Count(n => n.IsFirmwareUpdate);
            WasOutdatedWhenSucceeded = SelectedDevice?.IsFirmwareOutdated ?? false;
            LookupsWhenSucceeded = CountLookups?.Invoke() ?? -1;
        }

        /// <summary>How many times the confirmation before a write was asked (issue #564).</summary>
        public int Confirmations { get; private set; }

        /// <summary>Confirms at once: these facts are about the run, not the prompt in front of it (issue #564).</summary>
        public Task<bool> ConfirmFirmwareUpdateAsync(string title, string message, CancellationToken cancellationToken)
        {
            Confirmations++;
            return Task.FromResult(true);
        }

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
    #endregion
}
