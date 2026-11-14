using System.Collections.Concurrent;
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
/// Issue #570: what the user reads during and after a firmware update.
/// <list type="number">
/// <item>The status line is made from the report's <see cref="FirmwareUpdateProgress.State"/> in user
/// words, never from Core's <see cref="FirmwareUpdateProgress.CurrentOperation"/>
/// ("Programming record 4587 of 54957").</item>
/// <item>The success dialog names the device, the old version and the new version.</item>
/// <item>A successful run leaves the progress bar empty.</item>
/// </list>
/// No test here needs hardware: the PIC32 flash is a stub, and the device is the app's real
/// <see cref="SerialStreamingDevice"/> over a real Core device on a capturing transport, which is
/// the cheapest device that passes the coordinator's "is it connected" guard.
/// </summary>
public class FirmwareUpdateOutcomeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly RecordingHost _host = new();
    private readonly ReleaseDownloadService _downloads = new();
    private readonly ReportingUpdateService _updates = new();
    private readonly RecordingAppLogger _logger = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-firmware-outcome-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The version the post-restart reader gives: what the restarted device put in its status reply,
    /// or null when the version is not known. The reader itself is <c>PostRestartFirmwareVersion</c>,
    /// which #574 tests; here it is a seam.
    /// </summary>
    private string? _postRestartVersion;

    /// <summary>
    /// Runs in place of the reader, after <see cref="_postRestartVersion"/> is chosen. Lets a test
    /// see when the reader was asked and with which token.
    /// </summary>
    private Func<CancellationToken, Task>? _onReadPostRestartVersion;

    private int _postRestartReads;

    /// <summary>
    /// <c>canFlashWifiModule: false</c> and a device with no WINC module keep the run to the PIC32
    /// half, which is the half this ticket is about.
    /// </summary>
    private FirmwareUpdateCoordinator CreateCoordinator() =>
        new(_host,
            _updates,
            _downloads,
            NullLogger<FirmwareUpdateService>.Instance,
            _logger,
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: false,
            readPostRestartVersion: async (_, token) =>
            {
                Interlocked.Increment(ref _postRestartReads);
                if (_onReadPostRestartVersion != null)
                {
                    await _onReadPostRestartVersion(token);
                }

                return _postRestartVersion;
            });

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #region 1. Status text
    public static TheoryData<FirmwareUpdateState> AllStates()
    {
        var data = new TheoryData<FirmwareUpdateState>();
        foreach (var state in Enum.GetValues<FirmwareUpdateState>())
        {
            data.Add(state);
        }

        return data;
    }

    /// <summary>
    /// Every state Core can report gives a status text in user words, and none of them is the
    /// fallback: a state added to the enum without a text here fails this test instead of showing
    /// a generic line. Two states may share a text.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStates))]
    public void Every_state_has_a_user_text(FirmwareUpdateState state)
    {
        var text = FirmwareUpdateMessages.DescribeState(state);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.NotEqual(FirmwareUpdateMessages.UnknownStateText, text);
        Assert.DoesNotContain("record", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bootloader", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("application firmware", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A state from a later Core release still gives a sensible line.</summary>
    [Fact]
    public void An_unknown_state_gives_the_fallback_text()
    {
        Assert.Equal(
            FirmwareUpdateMessages.UnknownStateText,
            FirmwareUpdateMessages.DescribeState((FirmwareUpdateState)999));
    }

    /// <summary>
    /// The regression: the progress handler copied <see cref="FirmwareUpdateProgress.CurrentOperation"/>
    /// into the status line. It must now write the text for the report's state.
    /// </summary>
    [Fact]
    public async Task The_status_line_shows_the_state_text_and_not_the_core_operation_text()
    {
        using var harness = ConnectedDevice();
        _host.SelectedDevice = harness.Device;
        _host.FirmwareFilePath = FirmwareFile();
        _updates.Reports.Add(new FirmwareUpdateProgress
        {
            State = FirmwareUpdateState.Programming,
            PercentComplete = 26,
            CurrentOperation = "Programming record 4587 of 54957",
        });

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        // Progress<T> runs its handler on the thread pool when there is no synchronization
        // context, so the write can land after the run returns.
        var expected = FirmwareUpdateMessages.DescribeState(FirmwareUpdateState.Programming);
        Assert.True(
            SpinWait.SpinUntil(() => _host.StatusWrites.Contains(expected), Timeout),
            $"The status line never showed '{expected}'; saw: {string.Join(" | ", _host.StatusWrites)}");
        Assert.DoesNotContain(_host.StatusWrites, text => text.Contains("record", StringComparison.OrdinalIgnoreCase));
    }
    #endregion

    #region 2. Success text
    [Fact]
    public void The_success_text_names_the_device_and_both_versions()
    {
        var text = FirmwareUpdateMessages.UpdateSucceeded("Nq1", "3.8.0", "3.9.0");

        Assert.Equal("Nq1 now has firmware 3.9.0. The old version was 3.8.0.", text);
    }

    /// <summary>
    /// The device did not report a version after its restart: the text says the version is unknown
    /// and names no release.
    /// </summary>
    [Fact]
    public void An_unreported_new_version_is_unknown()
    {
        var text = FirmwareUpdateMessages.UpdateSucceeded("Nq1", "3.8.0", reportedVersion: "");

        Assert.Equal(
            "Nq1 has new firmware. The new version is unknown, because the device did not report it. " +
            "The old version was 3.8.0.",
            text);
    }

    [Fact]
    public void A_reinstall_of_the_same_version_says_so()
    {
        var text = FirmwareUpdateMessages.UpdateSucceeded("Nq1", "3.9.0", "3.9.0");

        Assert.Equal("Nq1 now has firmware 3.9.0. This is the same version as before.", text);
    }

    [Fact]
    public void An_unknown_old_version_is_left_out()
    {
        Assert.Equal("Nq1 now has firmware 3.9.0.", FirmwareUpdateMessages.UpdateSucceeded("Nq1", "", "3.9.0"));
    }

    /// <summary>
    /// Review findings on #577: the dialog must name the version from the restarted device's own
    /// status reply — not a release the app looked up, and not Core's shared metadata. Here the
    /// release feed says 3.9.0, Core's metadata still says 3.8.0, and the reply says 3.8.1.
    /// </summary>
    [Fact]
    public async Task The_success_dialog_names_the_version_from_the_post_restart_reply()
    {
        using var harness = ConnectedDevice(versionBeforeFlash: "3.8.0");
        _host.SelectedDevice = harness.Device;
        _downloads.LatestFirmwarePath = FirmwareFile();
        _downloads.LatestVersion = "3.9.0";
        _postRestartVersion = "3.8.1";

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal("Nq1 now has firmware 3.8.1. The old version was 3.8.0.", Assert.Single(_host.SuccessMessages));
    }

    /// <summary>
    /// The reply had no version (the reader gives null) while Core's metadata still holds the
    /// version from before the restart. The dialog must say unknown, and must not give the old
    /// version or the release as the installed result.
    /// </summary>
    [Fact]
    public async Task The_success_dialog_says_unknown_when_the_reply_has_no_version()
    {
        using var harness = ConnectedDevice(versionBeforeFlash: "3.8.0");
        _host.SelectedDevice = harness.Device;
        _downloads.LatestFirmwarePath = FirmwareFile();
        _downloads.LatestVersion = "3.9.0";
        _postRestartVersion = null;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        var message = Assert.Single(_host.SuccessMessages);
        Assert.Contains("The new version is unknown", message, StringComparison.Ordinal);
        Assert.DoesNotContain("3.9.0", message, StringComparison.Ordinal);
        Assert.DoesNotContain("now has firmware 3.8.0", message, StringComparison.Ordinal);
    }

    /// <summary>A manual <c>.hex</c> upload reports the device's version the same way.</summary>
    [Fact]
    public async Task A_manual_update_names_the_version_from_the_post_restart_reply()
    {
        using var harness = ConnectedDevice(versionBeforeFlash: "3.8.0");
        _host.SelectedDevice = harness.Device;
        _host.FirmwareFilePath = FirmwareFile();
        _postRestartVersion = "3.9.0";

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal("Nq1 now has firmware 3.9.0. The old version was 3.8.0.", Assert.Single(_host.SuccessMessages));
        Assert.Equal(string.Empty, _host.FirmwareFilePath);
    }

    /// <summary>
    /// The reader is asked only after the write has returned — a reply before that can come from the
    /// device before its restart.
    /// </summary>
    [Fact]
    public async Task The_version_is_read_after_the_write_returns()
    {
        using var harness = ConnectedDevice(versionBeforeFlash: "3.8.0");
        _host.SelectedDevice = harness.Device;
        _host.FirmwareFilePath = FirmwareFile();
        var writeReturned = false;
        bool? writeHadReturnedWhenRead = null;
        _updates.OnPic32Flash = () => writeReturned = true;
        _onReadPostRestartVersion = _ =>
        {
            writeHadReturnedWhenRead = writeReturned;
            return Task.CompletedTask;
        };

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.True(writeHadReturnedWhenRead);
    }

    /// <summary>The run's token reaches the reader, so CANCEL UPDATE stops the wait for the reply.</summary>
    [Fact]
    public async Task Cancel_stops_the_wait_for_the_post_restart_reply()
    {
        using var harness = ConnectedDevice(versionBeforeFlash: "3.8.0");
        _host.SelectedDevice = harness.Device;
        _host.FirmwareFilePath = FirmwareFile();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _onReadPostRestartVersion = async token =>
        {
            reading.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        };
        var coordinator = CreateCoordinator();

        var run = coordinator.UploadFirmwareAsync();
        await reading.Task.WaitAsync(Timeout);
        coordinator.CancelUpload();
        await run.WaitAsync(Timeout);

        Assert.Empty(_host.SuccessMessages);
        Assert.Contains("Firmware update canceled.", _host.StatusWrites);
    }

    /// <summary>
    /// The coordinator never writes Core's shared metadata: after a successful run the version the
    /// device reported at connect is still there, untouched.
    /// </summary>
    [Fact]
    public async Task A_successful_update_does_not_write_core_metadata()
    {
        using var harness = ConnectedDevice(versionBeforeFlash: "3.8.0");
        _host.SelectedDevice = harness.Device;
        _host.FirmwareFilePath = FirmwareFile();
        string? seenDuringWrite = null;
        _updates.OnPic32Flash = () => seenDuringWrite = harness.Core.Metadata.FirmwareVersion;
        _postRestartVersion = "3.9.0";

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Single(_host.SuccessMessages);
        Assert.Equal("3.8.0", seenDuringWrite);
        Assert.Equal("3.8.0", harness.Core.Metadata.FirmwareVersion);
        Assert.Equal(1, _postRestartReads);
    }

    /// <summary>The same after a failed run; a failed write also asks no reply.</summary>
    [Fact]
    public async Task A_failed_update_does_not_write_core_metadata()
    {
        using var harness = ConnectedDevice(versionBeforeFlash: "3.8.0");
        _host.SelectedDevice = harness.Device;
        _host.FirmwareFilePath = FirmwareFile();
        string? seenDuringWrite = null;
        _updates.OnPic32Flash = () =>
        {
            seenDuringWrite = harness.Core.Metadata.FirmwareVersion;
            throw new InvalidOperationException("flash failed");
        };

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Empty(_host.SuccessMessages);
        Assert.Equal("3.8.0", seenDuringWrite);
        Assert.Equal("3.8.0", harness.Core.Metadata.FirmwareVersion);
        Assert.Equal(0, _postRestartReads);
    }

    /// <summary>The friendly name, when the user set one, is the name the dialog uses.</summary>
    [Fact]
    public void The_device_name_prefers_the_friendly_name_then_the_part_number()
    {
        var device = new SerialStreamingDevice("COM-TEST-570");
        Assert.Equal("COM-TEST-570", FirmwareUpdateMessages.DeviceNameFor(device));

        device.Metadata.PartNumber = "Nq1";
        Assert.Equal("Nq1", FirmwareUpdateMessages.DeviceNameFor(device));

        device.FriendlyName = "Bench rig";
        Assert.Equal("Bench rig", FirmwareUpdateMessages.DeviceNameFor(device));
    }
    #endregion

    #region 3. Progress after the update
    /// <summary>
    /// The bar stayed at 100 % after a successful update. The stub sets it full, as the last
    /// progress report does, and the run must empty it.
    /// </summary>
    [Fact]
    public async Task A_successful_update_leaves_the_progress_bar_empty()
    {
        using var harness = ConnectedDevice();
        _host.SelectedDevice = harness.Device;
        _host.FirmwareFilePath = FirmwareFile();
        _updates.OnPic32Flash = () =>
        {
            _host.UploadFirmwareProgress = 100;
            _host.UploadWiFiProgress = 100;
        };

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Single(_host.SuccessMessages);
        Assert.Equal(0, _host.UploadFirmwareProgress);
        Assert.Equal(0, _host.UploadWiFiProgress);
    }

    /// <summary>
    /// The guard on the reset: a FAILED update keeps its bar, so the user can see how far it got
    /// before the error dialog. Only success empties it.
    /// </summary>
    [Fact]
    public async Task A_failed_update_does_not_empty_the_progress_bar()
    {
        using var harness = ConnectedDevice();
        _host.SelectedDevice = harness.Device;
        _host.FirmwareFilePath = FirmwareFile();
        _updates.OnPic32Flash = () =>
        {
            _host.UploadFirmwareProgress = 40;
            throw new InvalidOperationException("flash failed");
        };

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Empty(_host.SuccessMessages);
        Assert.Equal(40, _host.UploadFirmwareProgress);
    }
    #endregion

    #region Helpers
    /// <summary>A stand-in PIC32 image: the coordinator checks only that the file exists.</summary>
    private string FirmwareFile()
    {
        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, "firmware.hex");
        File.WriteAllText(path, ":00000001FF\n");
        return path;
    }

    /// <summary>
    /// A connected "Nq1". <paramref name="versionBeforeFlash"/> is the version the device reported
    /// at connect: the wrapper's copy (the tile) and Core's copy both hold it, as after a real
    /// connect's hydrate.
    /// </summary>
    private static Harness ConnectedDevice(string versionBeforeFlash = "")
    {
        var transport = new Daqifi.Avalonia.Tests.Device.CapturingTransport();
        var core = new DaqifiStreamingDevice("core", transport, NullLogger.Instance);
        core.Connect();
        core.Metadata.FirmwareVersion = versionBeforeFlash;
        var device = new SerialStreamingDevice("COM-TEST-570", core);
        device.Metadata.PartNumber = "Nq1";
        device.DeviceVersion = versionBeforeFlash;
        return new Harness(transport, core, device);
    }

    private sealed class Harness(
        Daqifi.Avalonia.Tests.Device.CapturingTransport transport,
        DaqifiStreamingDevice core,
        SerialStreamingDevice device) : IDisposable
    {
        public SerialStreamingDevice Device { get; } = device;

        public DaqifiStreamingDevice Core { get; } = core;

        public void Dispose()
        {
            transport.CloseStream();
            Core.Dispose();
            transport.Dispose();
        }
    }
    #endregion

    #region Fakes
    private sealed class ReleaseDownloadService : IFirmwareDownloadService
    {
        public string? LatestFirmwarePath { get; set; }

        public string? LatestVersion { get; set; }

        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(LatestFirmwarePath);

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default)
        {
            if (LatestVersion is null || !FirmwareVersion.TryParse(LatestVersion, out var version))
            {
                return Task.FromResult<FirmwareReleaseInfo?>(null);
            }

            return Task.FromResult<FirmwareReleaseInfo?>(new FirmwareReleaseInfo
            {
                Version = version,
                TagName = $"v{LatestVersion}",
                IsPreRelease = false,
            });
        }

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<(string ExtractedPath, string Version)?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void InvalidateCache() { }
    }

    /// <summary>
    /// PIC32 update service that flashes nothing. It sends <see cref="Reports"/> to the progress
    /// reporter, then runs <see cref="OnPic32Flash"/>.
    /// </summary>
    private sealed class ReportingUpdateService : IFirmwareUpdateService
    {
        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

        public List<FirmwareUpdateProgress> Reports { get; } = [];

        public Action? OnPic32Flash { get; set; }

#pragma warning disable CS0067 // Part of the interface; nothing in these tests raises it.
        public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress,
            string? targetDevicePath,
            string? targetLocationKey,
            CancellationToken cancellationToken = default)
        {
            foreach (var report in Reports)
            {
                progress?.Report(report);
            }

            OnPic32Flash?.Invoke();
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

    /// <summary>Records the status writes and the success dialog text the coordinator sends.</summary>
    private sealed class RecordingHost : IFirmwareUpdateHost
    {
        private readonly ConcurrentQueue<string> _statusWrites = new();

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
            set => _statusWrites.Enqueue(value);
        }

        public IReadOnlyCollection<string> StatusWrites => _statusWrites.ToArray();

        public List<string> SuccessMessages { get; } = [];

        public ObservableCollection<Notifications> Notifications { get; } = [];

        public DesktopStreamingDevice? DeviceBeingUpdated { get; set; }

        public void RefreshNotificationCount() { }

        public void ShowFirmwareError(string message) { }

        public void ShowFirmwareUpdateSucceeded(string message) => SuccessMessages.Add(message);

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
    #endregion
}
