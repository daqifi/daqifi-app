using System.Collections.ObjectModel;
using Daqifi.Core.Firmware;
using Daqifi.Core.Firmware.Winc;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Daqifi.Avalonia.Tests.ViewModels;
using DesktopStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins issue #330: the WiFi half of a firmware update must not be attempted on a machine that
/// cannot run it, and must not be offered there either.
///
/// <para>
/// Microchip ships the WINC1500 programmer only as the Windows batch file
/// <c>winc_flash_tool.cmd</c>, which the coordinator launches through an external process runner.
/// Off Windows that launch fails — and on the auto path it fails <em>after</em> the PIC32 image has
/// been written and CRC-verified, so the exception reached <c>UploadFirmwareAsync</c>'s generic
/// handler and the user was told "Firmware update failed. Please try again." for an update that had
/// in fact succeeded, with a retry that could never work on that machine.
/// </para>
///
/// <para>
/// The gate is an operating-system test rather than Core's <see cref="WincFlashToolLocator"/>, which
/// was the obvious candidate and is what the issue proposed.
/// <see cref="The_flash_tool_file_is_present_off_windows_too_so_locating_it_gates_nothing"/> is why:
/// the locator asks whether the tool <em>file</em> is present under a firmware path, and the WiFi
/// package this app downloads carries <c>winc_flash_tool.cmd</c> inside it, so the probe answers
/// "yes" on exactly the platforms the gate exists to stop.
/// </para>
///
/// <para>
/// No hardware and no flash: the platform decision is taken before anything is downloaded, so the
/// seam these tests observe is the injected download service — which on the unavailable branch must
/// never be reached at all.
/// </para>
///
/// <para>
/// Issue #567 moved the automatic path's module-version read ahead of that decision, because a
/// gate that ran first could not tell a current module from an outdated one and so told every
/// macOS and Linux user their module was not updated. The read is a query, not a flash; a scripted
/// <see cref="ILanChipInfoProvider"/> stands in for the module.
/// </para>
/// </summary>
[Collection(AppHostCollection.Name)]
public class WifiFlashPlatformGateTests : IDisposable
{
    private readonly RecordingHost _host = new();
    private readonly RecordingDownloadService _downloads = new();
    private readonly StubUpdateService _updates = new();
    private readonly RecordingAppLogger _logger = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-wifi-platform-gate-tests", Guid.NewGuid().ToString("N"));

    private FirmwareUpdateCoordinator CreateCoordinator(bool canFlashWifiModule) =>
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

    #region What the platform decision is, and why it is not the tool-file probe
    /// <summary>
    /// The capability the whole fix hangs off, stated once: Windows can flash the WINC module and
    /// nothing else can.
    /// </summary>
    [Fact]
    public void The_wifi_flash_capability_is_the_operating_system()
    {
        Assert.Equal(OperatingSystem.IsWindows(), FirmwareUpdateCoordinator.CanFlashWifiModule);
    }

    /// <summary>
    /// The reason the gate is not <see cref="WincFlashToolLocator.IsAvailable"/>, which the issue
    /// proposed and Core documents as the "can this machine flash the WiFi module?" probe.
    ///
    /// <para>
    /// The locator searches a firmware path for the tool file. The WiFi package this app hands it is
    /// the extracted source zipball of <c>daqifi/winc1500-Manual-UART-Firmware-Update</c>, whose tree
    /// is <c>winc/winc_flash_tool.cmd</c> alongside the firmware artifacts — the layout rebuilt
    /// below. That file is extracted on every platform, so the probe returns true on macOS and Linux
    /// as well, where the batch file still cannot execute. Off Windows this test therefore shows the
    /// two answers disagreeing, which is the whole reason the shipped gate reads the platform.
    /// </para>
    /// </summary>
    [Fact]
    public void The_flash_tool_file_is_present_off_windows_too_so_locating_it_gates_nothing()
    {
        var extracted = Path.Combine(_dataDirectory, "wifi-firmware-19.7.7");
        Directory.CreateDirectory(Path.Combine(extracted, "winc"));
        File.WriteAllText(Path.Combine(extracted, "winc", "winc_flash_tool.cmd"), "@echo off\n");

        var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

        // The probe says yes wherever this test runs, including the platforms that cannot run it.
        Assert.True(locator.IsAvailable(extracted));

        // And the app's own answer is the platform, so the two part company off Windows.
        Assert.Equal(OperatingSystem.IsWindows(), FirmwareUpdateCoordinator.CanFlashWifiModule);
    }
    #endregion

    #region The firmware run
    /// <summary>
    /// The unavailable branch: nothing is downloaded, no serial port is touched, and the run ends
    /// cleanly. Before the gate this call ran on into the WiFi download and the external tool launch,
    /// whose failure is what a caller reports as an update failure.
    /// </summary>
    [Fact]
    public async Task No_wifi_flash_is_attempted_where_the_tool_cannot_run()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);

        await coordinator.UpdateWifiModuleOnlyAsync(WincDevice());

        Assert.False(_downloads.WifiDownloadWasRequested);
        Assert.False(_host.IsFirmwareUploading);
    }

    /// <summary>
    /// The user is told why, in the app's own words, rather than being left with a step that quietly
    /// did not happen.
    /// </summary>
    [Fact]
    public async Task The_run_says_why_the_wifi_module_was_skipped()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);

        await coordinator.UpdateWifiModuleOnlyAsync(WincDevice());

        Assert.Contains(
            FirmwareUpdateCoordinator.WifiFlashUnavailableMessage,
            _host.Writes.Select(write => write.Text));

        // Written while the run is open, or the status line that renders it is hidden (issue #241).
        Assert.All(
            _host.Writes,
            write => Assert.True(
                write.WasUploading, $"'{write.Text}' was written while the status line was hidden."));
    }

    /// <summary>
    /// The negative control for the two above, and the reason this is a gate and not a deletion:
    /// where the tool can run, the WiFi step still runs. Reaching the download service is how far a
    /// flash gets before it needs hardware.
    /// </summary>
    [Fact]
    public async Task The_wifi_flash_still_runs_where_the_tool_can_run()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: true);

        // The download reports no package, which is the cheapest clean exit from a flash that has
        // genuinely started; the assertion is that it was asked at all.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateWifiModuleOnlyAsync(WincDevice()));

        Assert.True(_downloads.WifiDownloadWasRequested);
    }

    /// <summary>
    /// The half that survives the run. The status line is rendered only while the run is open
    /// (issue #241) and the success dialog closes the flyout carrying it, so a message written only
    /// there is gone by the time the user reads "Firmware update completed successfully". The
    /// connect-time WiFi probe is no fallback: it is debug-gated, so an ordinary macOS or Linux user
    /// has no standing outdated-WiFi notification to fall back on (Qodo round 1 on PR #337).
    /// </summary>
    [Fact]
    public async Task The_skipped_wifi_step_leaves_a_notification_that_outlives_the_run()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);

        await coordinator.UpdateWifiModuleOnlyAsync(WincDevice());

        var notification = Assert.Single(_host.Notifications);
        Assert.Contains(FirmwareUpdateCoordinator.WifiFlashUnavailableMessage, notification.Message);
        Assert.True(_host.NotificationCountRefreshed, "the notification badge was not re-synced.");
    }

    /// <summary>
    /// It is a standing limitation of the machine, not an event, so a second update must not stack a
    /// second copy of it in the flyout.
    /// </summary>
    [Fact]
    public async Task The_notification_is_not_stacked_by_a_second_run()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);
        var device = WincDevice();

        await coordinator.UpdateWifiModuleOnlyAsync(device);
        await coordinator.UpdateWifiModuleOnlyAsync(device);

        Assert.Single(_host.Notifications);
    }

    /// <summary>
    /// Deduplication is per device, not per sentence. <c>Name</c> is the model name Core's discovery
    /// reports, so a bench with two Nyquists carries the same one twice — and collapsing them onto a
    /// single notice would leave the second owner with nothing but "Firmware update completed
    /// successfully" (Qodo round 2 on PR #337).
    /// </summary>
    [Fact]
    public async Task Two_devices_with_the_same_name_each_get_their_own_notice()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);

        await coordinator.UpdateWifiModuleOnlyAsync(WincDevice("COM-TEST-330-A", "Nq1", "SN-0001"));
        await coordinator.UpdateWifiModuleOnlyAsync(WincDevice("COM-TEST-330-B", "Nq1", "SN-0002"));

        Assert.Equal(2, _host.Notifications.Count);
        Assert.Equal(
            new[] { "SN-0001", "SN-0002" },
            _host.Notifications.Select(notification => notification.DeviceSerialNo).Order());
    }

    /// <summary>
    /// The negative control: where the flash can run there is nothing to explain, so no notification
    /// is raised.
    /// </summary>
    [Fact]
    public async Task No_notification_is_raised_where_the_tool_can_run()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateWifiModuleOnlyAsync(WincDevice()));

        Assert.Empty(_host.Notifications);
    }

    /// <summary>
    /// The gate does not swallow the more specific answer. A device with no separately-flashable WINC
    /// module is skipped for that reason, not blamed on the platform — the ESP32 parts integrate WiFi
    /// into the SoC and would be described wrongly by a "requires Windows" message.
    /// </summary>
    [Fact]
    public async Task A_device_without_a_winc_module_is_not_told_it_needs_windows()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);

        await coordinator.UpdateWifiModuleOnlyAsync(new SerialStreamingDevice("COM-TEST-330-NOWINC"));

        Assert.DoesNotContain(
            FirmwareUpdateCoordinator.WifiFlashUnavailableMessage,
            _host.Writes.Select(write => write.Text));
    }
    #endregion

    #region The automatic path reads the module version before the platform gate (issue #567)
    /// <summary>
    /// The defect itself. On macOS and Linux the automatic WiFi step returned at the platform gate
    /// before it read the module version, so every update added "the WiFi module firmware was not
    /// updated" — also for a module already at the minimum version, which needed no update. The
    /// bench Nyquist 1 reported <c>19.7.7</c> before and after the 3.8.0 → 3.9.0 update that found
    /// this. A current module gets no notice, and nothing is downloaded or flashed.
    /// </summary>
    [Fact]
    public async Task A_current_module_gets_no_notice_where_the_tool_cannot_run()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);
        var device = ScriptedWincDevice.Answering(FirmwareUpdateCoordinator.MinimumWifiFirmwareVersion);

        await coordinator.UpdateWifiModuleAsync(device, CancellationToken.None);

        Assert.Empty(_host.Notifications);
        Assert.True(device.Queries > 0, "the module version was never read.");
        Assert.False(_downloads.WifiDownloadWasRequested);
        Assert.DoesNotContain(
            FirmwareUpdateCoordinator.WifiFlashUnavailableMessage,
            _host.Writes.Select(write => write.Text));
    }

    /// <summary>
    /// An outdated module on a computer that cannot flash it: one notice, and it says what the module
    /// has and what is necessary, so the user can tell a real gap from a formality. The flash is
    /// still never started — issue #330's rule that the device is not put into WiFi update mode
    /// where the tool cannot run. The download is the first step of a flash, so it not being asked
    /// for is the proof.
    /// </summary>
    [Fact]
    public async Task An_outdated_module_gets_one_notice_naming_both_versions()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);
        var device = ScriptedWincDevice.Answering("19.5.4");

        await coordinator.UpdateWifiModuleAsync(device, CancellationToken.None);

        var notice = Assert.Single(_host.Notifications);
        Assert.Contains("19.5.4", notice.Message);
        Assert.Contains(FirmwareUpdateCoordinator.MinimumWifiFirmwareVersion, notice.Message);
        Assert.Contains(FirmwareUpdateCoordinator.WifiFlashUnavailableMessage, notice.Message);
        Assert.Equal(device.DeviceSerialNo, notice.DeviceSerialNo);
        Assert.False(_downloads.WifiDownloadWasRequested);
    }

    /// <summary>
    /// A module that never answers cannot be called current, so the notice stays — worded as "could
    /// not read" rather than as a version the module never reported, and still naming the version
    /// that is necessary. Runs the app's real three-attempt probe budget, so it takes about four
    /// seconds.
    /// </summary>
    [Fact]
    public async Task An_unreadable_module_gets_one_notice_that_says_it_could_not_be_read()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);
        var device = ScriptedWincDevice.Answering(null);

        await coordinator.UpdateWifiModuleAsync(device, CancellationToken.None);

        var notice = Assert.Single(_host.Notifications);
        Assert.Contains("could not read", notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(FirmwareUpdateCoordinator.MinimumWifiFirmwareVersion, notice.Message);
        Assert.DoesNotContain("Unknown", notice.Message);
        Assert.Equal(3, device.Queries);
        Assert.False(_downloads.WifiDownloadWasRequested);
    }

    /// <summary>
    /// Core accepts any nonempty version string in the chip reply. A reply the app cannot parse as
    /// a version is not a version, so the notice must not quote it as "the module has firmware …":
    /// it gets the unreadable wording, the same as a module that did not answer. Both strings fail
    /// Core's pattern (at most three numeric parts, an optional letter suffix): a word, and a
    /// four-part dotted string such as an IP address.
    /// </summary>
    [Theory]
    [InlineData("not-a-version")]
    [InlineData("192.168.1.1")]
    public async Task A_version_the_app_cannot_parse_gets_the_unreadable_notice(string reply)
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);
        var device = ScriptedWincDevice.Answering(reply);

        await coordinator.UpdateWifiModuleAsync(device, CancellationToken.None);

        var notice = Assert.Single(_host.Notifications);
        Assert.Contains("could not read", notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(reply, notice.Message);
        Assert.Contains(FirmwareUpdateCoordinator.MinimumWifiFirmwareVersion, notice.Message);
        Assert.Equal(1, device.Queries);
        Assert.False(_downloads.WifiDownloadWasRequested);
    }

    /// <summary>
    /// The Windows half of the case above, to show the flash decision did not move: a reply the app
    /// cannot parse is still treated as needing a flash where the tool can run
    /// (<c>WifiFirmwareNeedsFlash</c> maps it to "flash"), so the download is requested and no notice
    /// is raised.
    /// </summary>
    [Theory]
    [InlineData("not-a-version")]
    [InlineData("192.168.1.1")]
    public async Task A_version_the_app_cannot_parse_is_still_flashed_where_the_tool_can_run(string reply)
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: true);
        var device = ScriptedWincDevice.Answering(reply);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateWifiModuleAsync(device, CancellationToken.None));

        Assert.True(_downloads.WifiDownloadWasRequested);
        Assert.Empty(_host.Notifications);
    }

    /// <summary>
    /// The text now varies with what the module reported, so deduplication can no longer match on
    /// the whole message. Two runs that read different versions still leave one notice per device,
    /// and it carries the latest reading.
    /// </summary>
    [Fact]
    public async Task A_second_reading_replaces_the_notice_rather_than_stacking_a_second()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);
        var device = ScriptedWincDevice.Answering("19.5.4");

        await coordinator.UpdateWifiModuleAsync(device, CancellationToken.None);
        device.Answer = Chip("19.6.0");
        await coordinator.UpdateWifiModuleAsync(device, CancellationToken.None);

        var notice = Assert.Single(_host.Notifications);
        Assert.Contains("19.6.0", notice.Message);
        Assert.DoesNotContain("19.5.4", notice.Message);
    }

    /// <summary>
    /// A notice is withdrawn once it stops being true: a later reading of a current module removes
    /// whatever an earlier run left. The earlier run here reads an outdated version, which raises the
    /// same notice as an unreadable module through the same lookup, without spending the real
    /// four-second retry budget a second time —
    /// <see cref="An_unreadable_module_gets_one_notice_that_says_it_could_not_be_read"/> is the one
    /// test that runs it.
    /// </summary>
    [Fact]
    public async Task A_later_current_reading_withdraws_an_earlier_notice()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);
        var device = ScriptedWincDevice.Answering("19.5.4");

        await coordinator.UpdateWifiModuleAsync(device, CancellationToken.None);
        Assert.Single(_host.Notifications);

        device.Answer = Chip(FirmwareUpdateCoordinator.MinimumWifiFirmwareVersion);
        await coordinator.UpdateWifiModuleAsync(device, CancellationToken.None);

        Assert.Empty(_host.Notifications);
    }

    /// <summary>
    /// Withdrawal is scoped like the notice: per device, and only the WiFi notice. Another board's
    /// notice and this board's other notifications stay.
    /// </summary>
    [Fact]
    public async Task Withdrawing_leaves_other_devices_and_other_notifications_alone()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: false);
        var other = ScriptedWincDevice.Answering("19.5.4", serial: "SN-0002");
        var device = ScriptedWincDevice.Answering("19.5.4", serial: "SN-0001");
        var unrelated = new Notifications { Message = "Nq1: unrelated", DeviceSerialNo = "SN-0001" };
        _host.Notifications.Add(unrelated);

        await coordinator.UpdateWifiModuleAsync(other, CancellationToken.None);
        await coordinator.UpdateWifiModuleAsync(device, CancellationToken.None);
        device.Answer = Chip(FirmwareUpdateCoordinator.MinimumWifiFirmwareVersion);
        await coordinator.UpdateWifiModuleAsync(device, CancellationToken.None);

        Assert.Equal(2, _host.Notifications.Count);
        Assert.Contains(unrelated, _host.Notifications);
        Assert.Contains(_host.Notifications, notice => notice.DeviceSerialNo == "SN-0002");
    }

    /// <summary>
    /// The negative control on the computer that can flash: a current module is still skipped there
    /// with no notice and no download. Moving the check ahead of the gate must not change Windows.
    /// </summary>
    [Fact]
    public async Task A_current_module_is_still_skipped_where_the_tool_can_run()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: true);
        var device = ScriptedWincDevice.Answering(FirmwareUpdateCoordinator.MinimumWifiFirmwareVersion);

        await coordinator.UpdateWifiModuleAsync(device, CancellationToken.None);

        Assert.Empty(_host.Notifications);
        Assert.False(_downloads.WifiDownloadWasRequested);
    }

    /// <summary>
    /// And an outdated module there still goes on to the flash. The download reports no package,
    /// the cheapest clean exit from a flash that has started.
    /// </summary>
    [Fact]
    public async Task An_outdated_module_is_still_flashed_where_the_tool_can_run()
    {
        var coordinator = CreateCoordinator(canFlashWifiModule: true);
        var device = ScriptedWincDevice.Answering("19.5.4");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateWifiModuleAsync(device, CancellationToken.None));

        Assert.True(_downloads.WifiDownloadWasRequested);
        Assert.Empty(_host.Notifications);
    }
    #endregion

    #region The standalone FLASH WIFI affordance
    /// <summary>
    /// The button must be enabled exactly where the flash can happen. It matters beyond the greying:
    /// <c>UpdateWifiFirmwareOnly</c> reports success when the coordinator returns, so a command that
    /// started a run the gate skips would announce "WiFi firmware update completed successfully" for
    /// a flash that never occurred — the one outcome worse than a false failure.
    /// </summary>
    [Fact]
    public void The_flash_wifi_button_is_enabled_only_where_the_module_can_be_flashed()
    {
        var shell = NewShell();
        shell.SelectedDevice = WincDevice();

        Assert.Equal(
            FirmwareUpdateCoordinator.CanFlashWifiModule,
            shell.UpdateWifiFirmwareOnlyCommand.CanExecute(null));
    }

    /// <summary>
    /// The negative control for the assertion above: the platform term was added to a guard that
    /// already had device terms, and must not have widened it into "enabled whenever Windows". A
    /// device with no WINC module stays out regardless.
    /// </summary>
    [Fact]
    public void A_device_without_a_winc_module_still_cannot_be_flashed()
    {
        var shell = NewShell();
        shell.SelectedDevice = new SerialStreamingDevice("COM-TEST-330-NOWINC");

        Assert.False(shell.UpdateWifiFirmwareOnlyCommand.CanExecute(null));
    }

    /// <summary>
    /// Both device panes explain the disabled button. These bindings are resolved by reflection, so
    /// nothing in either head's build can see them break — see <see cref="BindingFacts"/>.
    /// </summary>
    [Theory]
    [InlineData("Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/DevicesPanePrototype.axaml")]
    [InlineData("Daqifi.Avalonia/Views/Mobile/DevicesMobileView.axaml")]
    public void The_device_panes_explain_why_the_button_is_disabled(string viewPath)
    {
        BindingFacts.AssertBinds(viewPath, "{Binding Shell.WifiFlashUnavailableMessage}");
        BindingFacts.AssertBinds(viewPath, "IsVisible=\"{Binding !Shell.CanFlashWifiModule}\"");

        BindingFacts.AssertExposes(typeof(DaqifiViewModel), nameof(DaqifiViewModel.CanFlashWifiModule));
        BindingFacts.AssertExposes(
            typeof(DaqifiViewModel), nameof(DaqifiViewModel.WifiFlashUnavailableMessage));
    }
    #endregion

    #region Helpers
    /// <summary>
    /// The shell view model, on the throwaway data directory the assembly's module initializer points
    /// <c>DAQIFI_DATA_DIR</c> at. <c>InitializeMobile</c> is idempotent.
    /// </summary>
    private static DaqifiViewModel NewShell()
    {
        Daqifi.Desktop.App.InitializeMobile();
        return new DaqifiViewModel(new NullDialogService());
    }

    /// <summary>A USB device that reports a separately-flashable WINC module.</summary>
    private static SerialStreamingDevice WincDevice()
    {
        var device = new SerialStreamingDevice("COM-TEST-330");
        device.Metadata.Capabilities.HasWincWifiModule = true;
        return device;
    }

    /// <summary>
    /// The same, with the identity Core's discovery would have filled in — two boards of one model
    /// share <c>Name</c> and differ only by serial.
    /// </summary>
    private static SerialStreamingDevice WincDevice(string portName, string deviceName, string serial)
    {
        var device = new SerialStreamingDevice(portName, deviceName, serial, firmwareVersion: "3.8.0");
        device.Metadata.Capabilities.HasWincWifiModule = true;
        return device;
    }

    private static LanChipInfo Chip(string version) =>
        new() { ChipId = 0x1503A0, FwVersion = version, BuildDate = "2020-01-01" };
    #endregion

    #region Fakes
    /// <summary>
    /// A USB device with a WINC module whose answer to the version query is scripted. The real
    /// <see cref="SerialStreamingDevice.GetLanChipInfoAsync"/> asks the Core device, which a unit
    /// test does not have; re-implementing <see cref="ILanChipInfoProvider"/> here routes the
    /// coordinator's <c>is ILanChipInfoProvider</c> probe to the script instead. With no Core device,
    /// the coordinator's <c>PowerOnWifiModule</c> call sends nothing and logs a warning.
    /// </summary>
    private sealed class ScriptedWincDevice : SerialStreamingDevice, ILanChipInfoProvider
    {
        private ScriptedWincDevice(string serial)
            : base("COM-TEST-567", "Nq1", serial, firmwareVersion: "3.9.0")
        {
            Metadata.Capabilities.HasWincWifiModule = true;
        }

        /// <summary>What the module answers; null is a module that does not answer.</summary>
        public LanChipInfo? Answer { get; set; }

        /// <summary>How many times the module was asked for its version.</summary>
        public int Queries { get; private set; }

        public static ScriptedWincDevice Answering(string? version, string serial = "SN-0567") =>
            new(serial) { Answer = version is null ? null : Chip(version) };

        public new Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default)
        {
            Queries++;
            return Task.FromResult(Answer);
        }
    }

    /// <summary>
    /// Download service that records whether the WiFi package was ever asked for — the observable
    /// difference between a flash that started and one the gate stopped — and reports no package, the
    /// cheapest clean exit from one that did.
    /// </summary>
    private sealed class RecordingDownloadService : IFirmwareDownloadService
    {
        public bool WifiDownloadWasRequested { get; private set; }

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            WifiDownloadWasRequested = true;
            return Task.FromResult<(string, string)?>(null);
        }

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
            CancellationToken cancellationToken = default) => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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

    /// <summary>
    /// Host seam recording every status write together with whether the run was open at the time,
    /// which is the condition the views gate the status line on.
    /// </summary>
    private sealed class RecordingHost : IFirmwareUpdateHost
    {
        public List<(string Text, bool WasUploading)> Writes { get; } = [];

        public DesktopStreamingDevice? SelectedDevice { get; set; }

        public IReadOnlyList<DesktopStreamingDevice> ConnectedDevices { get; } = [];

        public string FirmwareFilePath { get; set; } = string.Empty;

        public bool SelectedDeviceSupportsFirmwareUpdate { get; set; }

        public bool IsFirmwareUploading { get; set; }

        public bool IsUploadComplete { get; set; }

        public bool HasErrorOccured { get; set; }

        public int UploadFirmwareProgress { get; set; }

        public int UploadWiFiProgress { get; set; }

        public string FirmwareUpdateStatusText
        {
            set => Writes.Add((value, IsFirmwareUploading));
        }

        public ObservableCollection<Notifications> Notifications { get; } = [];

        public DesktopStreamingDevice? DeviceBeingUpdated { get; set; }

        /// <summary>The badge re-sync a notification the user is meant to see depends on.</summary>
        public bool NotificationCountRefreshed { get; private set; }

        public void RefreshNotificationCount() => NotificationCountRefreshed = true;

        public void ShowFirmwareError(string message) { }

        public void ShowFirmwareUpdateSucceeded(string message) { }

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
    #endregion
}
