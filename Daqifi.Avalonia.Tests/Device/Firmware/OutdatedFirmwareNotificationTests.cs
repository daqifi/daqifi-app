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
    private readonly LatestIs390DownloadService _downloads = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-outdated-notice-tests", Guid.NewGuid().ToString("N"));

    private FirmwareUpdateCoordinator CreateCoordinator() =>
        new(_host,
            new NoFlashUpdateService(),
            _downloads,
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
        Assert.Contains("Nq1 (serial 7E2815916200E898) has firmware 3.8.0. Version 3.9.0 is available.", messages);
        Assert.Contains("Nq1 (serial 7E2815916200E899) has firmware 3.8.0. Version 3.9.0 is available.", messages);
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

    #region A notice that already exists follows the text it should have (review of PR #576)
    /// <summary>
    /// The rule: the serial is in the text while another connected device has the same name, and
    /// only then. The check runs again on every connect and disconnect, so the first device's notice
    /// gains the serial when the second Nq1 connects and loses it when that device goes away.
    /// </summary>
    [Fact]
    public async Task The_first_notice_gains_the_serial_when_a_second_Nq1_connects_and_loses_it_when_it_leaves()
    {
        var first = Nq1("9090539562006014104", "3.8.0");
        var second = Nq1("9090539562006014105", "3.8.0");
        var coordinator = CreateCoordinator();

        _host.SetConnectedDevices(first);
        await coordinator.RefreshFirmwareUpdatesAsync();
        Assert.Equal("Nq1 has firmware 3.8.0. Version 3.9.0 is available.", NoticeFor(first).Message);

        _host.SetConnectedDevices(first, second);
        await coordinator.RefreshFirmwareUpdatesAsync();
        Assert.Equal("Nq1 (serial 7E2815916200E898) has firmware 3.8.0. Version 3.9.0 is available.", NoticeFor(first).Message);
        Assert.Equal(2, _host.Notifications.Count);

        _host.SetConnectedDevices(first);
        await coordinator.RefreshFirmwareUpdatesAsync();
        Assert.Equal("Nq1 has firmware 3.8.0. Version 3.9.0 is available.", NoticeFor(first).Message);
    }

    [Fact]
    public async Task A_newer_release_updates_the_text_of_an_existing_notice()
    {
        var device = Nq1("9090539562006014104", "3.8.0");
        _host.SetConnectedDevices(device);
        var coordinator = CreateCoordinator();
        await coordinator.RefreshFirmwareUpdatesAsync();

        _downloads.Latest = Release(3, 10, 0);
        await coordinator.RefreshFirmwareUpdatesAsync();

        var notice = Assert.Single(_host.Notifications);
        Assert.Equal("Nq1 has firmware 3.8.0. Version 3.10.0 is available.", notice.Message);
    }

    /// <summary>
    /// The replacement is a new item (the model's text is init-only). It stays where the old one
    /// was, it is still a firmware notice for the same serial, and its button still opens that device.
    /// </summary>
    [Fact]
    public async Task A_replaced_notice_keeps_its_place_and_its_Update_button_opens_that_device()
    {
        var first = Nq1("9090539562006014104", "3.8.0");
        var second = Nq1("9090539562006014105", "3.8.0");
        var coordinator = CreateCoordinator();
        _host.Notifications.Add(new Notifications { Message = "An unrelated notice above it" });
        _host.SetConnectedDevices(first);
        await coordinator.RefreshFirmwareUpdatesAsync();
        var before = NoticeFor(first);
        var index = _host.Notifications.IndexOf(before);

        _host.SetConnectedDevices(first, second);
        await coordinator.RefreshFirmwareUpdatesAsync();

        var after = NoticeFor(first);
        Assert.NotSame(before, after);
        Assert.Equal(index, _host.Notifications.IndexOf(after));
        Assert.True(after.IsFirmwareUpdate);
        Assert.Equal("Update", after.ActionText);
        after.Action!.Execute(null);
        Assert.Same(first, Assert.Single(_host.FirmwareSectionRequests));
    }

    /// <summary>A notice whose text did not change is left alone, so the list does not flicker on every connect.</summary>
    [Fact]
    public async Task A_notice_whose_text_did_not_change_is_not_replaced()
    {
        var device = Nq1("9090539562006014104", "3.8.0");
        _host.SetConnectedDevices(device);
        var coordinator = CreateCoordinator();
        await coordinator.RefreshFirmwareUpdatesAsync();
        var before = Assert.Single(_host.Notifications);

        await coordinator.RefreshFirmwareUpdatesAsync();

        Assert.Same(before, Assert.Single(_host.Notifications));
    }
    #endregion

    #region The check records each device's own comparison (review of PR #576)
    [Fact]
    public async Task The_check_records_each_device_comparison_against_the_newest_version()
    {
        var outdated = Nq1("9090539562006014104", "3.8.0");
        var current = Nq1("9090539562006014105", "3.9.0");
        _host.SetConnectedDevices(outdated, current);

        await CreateCoordinator().RefreshFirmwareUpdatesAsync();

        Assert.Equal(
            [(outdated, "3.8.0", "3.9.0", true), (current, "3.9.0", "3.9.0", false)],
            _host.Comparisons);
    }

    /// <summary>
    /// The release lookup fails: nothing is compared or recorded, so the drawer keeps showing only
    /// the installed version and never says "up to date". (Since the comparison is made locally from
    /// the release the lookup returned, the lookup is the part of a check that can fail.)
    /// </summary>
    [Fact]
    public async Task A_failed_lookup_records_nothing()
    {
        _host.SetConnectedDevices(Nq1("9090539562006014104", "3.9.0"));
        _downloads.ThrowOnLookup = true;
        var coordinator = CreateCoordinator();

        await coordinator.RefreshFirmwareUpdatesAsync();

        Assert.Equal(string.Empty, coordinator.LatestFirmwareVersion);
        Assert.Empty(_host.Comparisons);
    }
    /// <summary>
    /// Review of PR #576 (third finding): the device reports a new installed version while the check
    /// is pending. The comparison, the record and the notice all use the version read once for the
    /// comparison — here the one reported during the lookup.
    /// </summary>
    [Fact]
    public async Task The_notice_states_the_version_that_was_compared_not_one_reported_during_the_check()
    {
        var device = Nq1("9090539562006014104", "3.8.0");
        _host.SetConnectedDevices(device);
        _downloads.WhileLookingUp = () => { device.DeviceVersion = "3.7.0"; return Task.CompletedTask; };

        await CreateCoordinator().RefreshFirmwareUpdatesAsync();

        Assert.Equal("Nq1 has firmware 3.7.0. Version 3.9.0 is available.", Assert.Single(_host.Notifications).Message);
        Assert.Equal((device, "3.7.0", "3.9.0", true), Assert.Single(_host.Comparisons));
    }

    /// <summary>
    /// Review of PR #576 (third finding): the release that Core's own <c>CheckForUpdateAsync</c>
    /// sees (3.10.0, from its own lookup) differs from the release this check found (3.9.0). The
    /// decision, the record and the notice must all name the release that was compared: a device on
    /// 3.9.0 is current against 3.9.0, and gets no notice saying "Version 3.9.0 is available".
    /// </summary>
    [Fact]
    public async Task The_record_and_the_notice_name_the_release_that_was_compared()
    {
        var current = Nq1("9090539562006014104", "3.9.0");
        var outdated = Nq1("9090539562006014105", "3.8.0");
        outdated.FriendlyName = "Second";
        _host.SetConnectedDevices(current, outdated);
        _downloads.ReleaseSeenByCheck = Release(3, 10, 0);

        await CreateCoordinator().RefreshFirmwareUpdatesAsync();

        Assert.Equal(
            [(current, "3.9.0", "3.9.0", false), (outdated, "3.8.0", "3.9.0", true)],
            _host.Comparisons);
        Assert.Equal("Second has firmware 3.8.0. Version 3.9.0 is available.", Assert.Single(_host.Notifications).Message);
    }

    /// <summary>
    /// The local decision gives the same answer as Core 1.9.1's <c>CheckForUpdateAsync</c>
    /// (<c>!hasCurrent || latest.Version &gt; deviceVersion</c>), including for a version that does
    /// not parse and for pre-releases, which <c>FirmwareVersion</c> ranks below their release.
    /// </summary>
    [Theory]
    [InlineData("3.8.0", true)]
    [InlineData("3.9.0", false)]
    [InlineData("v3.9.0", false)]
    [InlineData("3.10.0", false)]
    [InlineData("3.9.0rc1", true)]
    [InlineData("3.9.0b2", true)]
    [InlineData("3.10.0b1", false)]
    [InlineData("", true)]
    [InlineData("garbage", true)]
    [InlineData("99999999999.0.0", true)]
    public void The_local_decision_matches_Core(string installed, bool olderThan390)
    {
        Assert.Equal(olderThan390, FirmwareUpdateCoordinator.IsOlderThan(installed, Release(3, 9, 0)));
    }

    /// <summary>
    /// A second check finds a newer release while the first one is still comparing. The first check's
    /// result must carry the release its own lookup found, not the one the second check left behind.
    /// </summary>
    [Fact]
    public async Task A_result_carries_the_release_that_its_own_lookup_found()
    {
        var device = Nq1("9090539562006014104", "3.8.0");
        _host.SetConnectedDevices(device);
        var coordinator = CreateCoordinator();
        var once = true;
        _downloads.WhileLookingUp = async () =>
        {
            if (!once) return;
            once = false;
            _downloads.Latest = Release(3, 10, 0);
            await coordinator.RefreshFirmwareUpdatesAsync();
        };

        await coordinator.RefreshFirmwareUpdatesAsync();

        // The second (inner) check finishes first; the first check's result comes after it.
        Assert.Equal("3.10.0", _host.Comparisons[0].Latest);
        Assert.Equal("3.9.0", _host.Comparisons[1].Latest);
    }

    /// <summary>
    /// A device that reports a new version after the check recorded its result: the record keeps the
    /// version that was compared, and the drawer rule (version now = recorded version) stops using it.
    /// </summary>
    [Fact]
    public async Task The_result_records_the_installed_version_that_was_compared()
    {
        var device = Nq1("9090539562006014104", "3.8.0");
        _host.SetConnectedDevices(device);

        await CreateCoordinator().RefreshFirmwareUpdatesAsync();
        device.DeviceVersion = "3.9.0";

        Assert.Equal((device, "3.8.0", "3.9.0", true), Assert.Single(_host.Comparisons));
    }
    #endregion

    #region Helpers and fakes
    private Notifications NoticeFor(DesktopStreamingDevice device) =>
        _host.Notifications.Single(n => n.IsFirmwareUpdate && n.DeviceSerialNo == device.DeviceSerialNo);

    internal static FirmwareReleaseInfo Release(int major, int minor, int patch) => new()
    {
        Version = new FirmwareVersion(major, minor, patch, PreLabel: null, PreNumber: 0),
        TagName = $"v{major}.{minor}.{patch}",
        IsPreRelease = false
    };

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

        public List<(DesktopStreamingDevice Device, string Installed, string Latest, bool IsOutdated)> Comparisons { get; } = [];

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
        public void RecordFirmwareComparison(
            DesktopStreamingDevice device, string installedVersion, string latestVersion, bool isOutdated) =>
            Comparisons.Add((device, installedVersion, latestVersion, isOutdated));
    }

    /// <summary>
    /// Reports <see cref="Latest"/> (3.9.0 unless a test changes it) as the newest release.
    /// <see cref="CheckForUpdateAsync"/> applies Core 1.9.1's rule against
    /// <see cref="ReleaseSeenByCheck"/> (or <see cref="Latest"/>). <see cref="ThrowOnLookup"/> makes
    /// the release lookup fail.
    /// </summary>
    internal sealed class LatestIs390DownloadService : IFirmwareDownloadService
    {
        public FirmwareReleaseInfo Latest { get; set; } = Release(3, 9, 0);

        public bool ThrowOnLookup { get; set; }

        /// <summary>The release lookup finds nothing (no published release).</summary>
        public bool NoRelease { get; set; }

        /// <summary>
        /// Runs while the release lookup is pending — after its answer is decided, before it is
        /// returned. The lookup is the only wait left in a check, so this is where a test changes the
        /// world under a check that is still running.
        /// </summary>
        public Func<Task>? WhileLookingUp { get; set; }

        /// <summary>
        /// The release that Core's own <c>CheckForUpdateAsync</c> would compare with. Core 1.9.1
        /// looks the release up again inside that method, so it can differ from what the lookup
        /// returned. Null means the same release as <see cref="Latest"/>.
        /// </summary>
        public FirmwareReleaseInfo? ReleaseSeenByCheck { get; set; }

        public async Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false, CancellationToken cancellationToken = default)
        {
            if (ThrowOnLookup) throw new HttpRequestException("firmware release lookup failed");
            var found = NoRelease ? null : Latest;
            if (WhileLookingUp != null) await WhileLookingUp();
            return found;
        }

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString, bool includePreRelease = false, CancellationToken cancellationToken = default)
        {
            var latest = ReleaseSeenByCheck ?? Latest;
            return Task.FromResult(new FirmwareUpdateCheckResult
            {
                UpdateAvailable = !FirmwareVersion.TryParse(deviceVersionString, out var installed)
                                  || latest.Version > installed,
                LatestRelease = latest
            });
        }

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
    internal sealed class NoFlashUpdateService : IFirmwareUpdateService
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
