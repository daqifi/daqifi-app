using System.Collections.ObjectModel;
using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Avalonia.Tests.Views;
using Daqifi.Core.Firmware;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DesktopStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins issue #308: while the WiFi version check before a flash waits for a module that is still
/// starting, the status line says that the app is retrying.
///
/// <para>
/// The check runs on the automatic path after a PIC32 update, while the firmware status line is on
/// screen. A WINC that is still starting can miss the first attempts, and with the app's budget
/// (three attempts, two seconds apart) the probe can take about ten seconds. The app used to set
/// "Waiting for device to finish starting up before checking WiFi firmware version..." between
/// attempts. #310 deleted that, together with the app's own copy of the retry loop, because Core's
/// loop gave no per-attempt signal. Core 1.10.0 reports each attempt that reads nothing
/// (<see cref="LanChipInfoRetryOptions.Progress"/>, daqifi-core#745), so the message is back and
/// the loop is still Core's.
/// </para>
///
/// <para>
/// The coordinator runs on a <see cref="SerialTestUi"/> thread. It has a synchronization context,
/// as Avalonia's dispatcher does, so the <see cref="Progress{T}"/> sink posts to it in the order
/// production does. Thus these tests can assert the order the user sees, not only that a write
/// occurred at some time. No hardware: a scripted <see cref="ILanChipInfoProvider"/> answers for
/// the module. Two tests spend the app's real two-second pause, so they take about four and two
/// seconds.
/// </para>
/// </summary>
[Collection(AppHostCollection.Name)]
public class WifiChipInfoRetryStatusTests : IDisposable
{
    private const string Checking = "Checking WiFi firmware version...";

    private const string Waiting =
        "Waiting for device to finish starting up before checking WiFi firmware version";

    private readonly SerialTestUi _ui = new();
    private readonly RecordingHost _host = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-wifi-retry-status-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A computer that cannot flash the module, so that the run ends cleanly at the platform gate
    /// after the version check, and nothing is downloaded.
    /// </summary>
    private FirmwareUpdateCoordinator CreateCoordinator() =>
        new(_host,
            new UnreachableUpdateService(),
            new UnreachableDownloadService(),
            NullLogger<FirmwareUpdateService>.Instance,
            new RecordingAppLogger(),
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: false);

    public void Dispose()
    {
        _ui.Dispose();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The regression. A module that never answers is asked three times. Before the second and the
    /// third attempt the user sees that the app is waiting, and which attempt comes next. After the
    /// third attempt there is no retry to announce, so the next line is the outcome. Without the
    /// sink, the line stayed on "Checking WiFi firmware version..." for the whole probe.
    /// </summary>
    [Fact]
    public async Task Each_retry_of_a_silent_module_is_on_the_status_line_before_the_outcome()
    {
        var device = ScriptedWincDevice.That([null, null, null]);

        await _ui.RunAsync(() => CreateCoordinator().UpdateWifiModuleAsync(device, CancellationToken.None));

        Assert.Equal(
            [
                Checking,
                $"{Waiting} (attempt 2 of 3)...",
                $"{Waiting} (attempt 3 of 3)...",
                FirmwareUpdateCoordinator.WifiFlashUnavailableMessage,
            ],
            await _ui.Get(() => _host.Writes.ToList()));
        Assert.Equal(3, device.Queries);
    }

    /// <summary>
    /// The usual case after a PIC32 update (#144): the module misses the first attempt and answers
    /// the second. The user sees one retry, and the line then ends on the version, not on the retry.
    /// </summary>
    [Fact]
    public async Task A_module_that_answers_the_second_attempt_ends_on_its_version()
    {
        var device = ScriptedWincDevice.That([null, Chip(FirmwareUpdateCoordinator.MinimumWifiFirmwareVersion)]);

        await _ui.RunAsync(() => CreateCoordinator().UpdateWifiModuleAsync(device, CancellationToken.None));

        Assert.Equal(
            [
                Checking,
                $"{Waiting} (attempt 2 of 3)...",
                $"WiFi firmware already up to date ({FirmwareUpdateCoordinator.MinimumWifiFirmwareVersion}).",
            ],
            await _ui.Get(() => _host.Writes.ToList()));
        Assert.Equal(2, device.Queries);
    }

    /// <summary>
    /// The user cancels while the module is being asked, and the attempt reads nothing. Core still
    /// reports that attempt, but no retry follows: the cancellation ends the probe. So the status
    /// line must not announce one. The run's own cancel handling writes what the user reads next.
    /// </summary>
    [Fact]
    public async Task A_run_cancelled_during_an_attempt_does_not_announce_a_retry()
    {
        using var cts = new CancellationTokenSource();
        var device = ScriptedWincDevice.That([null]);
        device.OnQuery = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _ui.RunAsync(() => CreateCoordinator().UpdateWifiModuleAsync(device, cts.Token)));

        Assert.Equal([Checking], await _ui.Get(() => _host.Writes.ToList()));
        Assert.Equal(1, device.Queries);
    }

    /// <summary>
    /// The other half of the ticket: the connect-time probe in <c>DaqifiViewModel</c> must not write
    /// the status line (issue #241). It uses the shared policy as is, so the shared policy must give
    /// Core no sink. The flash path adds its sink to a copy.
    /// </summary>
    [Fact]
    public void The_shared_policy_that_the_connect_time_probe_uses_reports_to_no_one()
    {
        Assert.Null(FirmwareUpdateCoordinator.WifiChipInfoRetryOptions.Progress);
    }

    #region Helpers
    private static LanChipInfo Chip(string version) =>
        new() { ChipId = 0x1503A0, FwVersion = version, BuildDate = "2020-01-01" };
    #endregion

    #region Fakes
    /// <summary>
    /// A USB device with a WINC module whose answer to each version query is scripted; null is an
    /// attempt that reads nothing. Re-implementing <see cref="ILanChipInfoProvider"/> sends the
    /// coordinator's probe to the script instead of to a Core device. With no Core device,
    /// <c>PowerOnWifiModule</c> sends nothing.
    /// </summary>
    private sealed class ScriptedWincDevice : SerialStreamingDevice, ILanChipInfoProvider
    {
        private readonly LanChipInfo?[] _answers;

        private ScriptedWincDevice(LanChipInfo?[] answers)
            : base("COM-TEST-308", "Nq1", "SN-0308", firmwareVersion: "3.9.0")
        {
            _answers = answers;
            Metadata.Capabilities.HasWincWifiModule = true;
        }

        /// <summary>Answers in order; an attempt past the end of the script reads nothing.</summary>
        public static ScriptedWincDevice That(LanChipInfo?[] answers) => new(answers);

        /// <summary>How many times the module was asked for its version.</summary>
        public int Queries { get; private set; }

        /// <summary>Runs during each query, before it answers.</summary>
        public Action? OnQuery { get; set; }

        public new Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default)
        {
            var answer = Queries < _answers.Length ? _answers[Queries] : null;
            Queries++;
            OnQuery?.Invoke();
            return Task.FromResult(answer);
        }
    }

    /// <summary>Records every status write, in order.</summary>
    private sealed class RecordingHost : IFirmwareUpdateHost
    {
        public List<string> Writes { get; } = [];

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
            set => Writes.Add(value);
        }

        public ObservableCollection<Notifications> Notifications { get; } = [];

        public DesktopStreamingDevice? DeviceBeingUpdated { get; set; }

        public void RefreshNotificationCount() { }

        public void ShowFirmwareError(string message) { }

        public void ShowFirmwareUpdateSucceeded(string message) { }

        public Task<bool> ConfirmFirmwareUpdateAsync(string title, string message, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>The version check never downloads; a call here fails the test.</summary>
    private sealed class UnreachableDownloadService : IFirmwareDownloadService
    {
        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InvalidateCache() { }
    }

    /// <summary>The version check flashes nothing; a call here fails the test.</summary>
    private sealed class UnreachableUpdateService : IFirmwareUpdateService
    {
        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

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
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateWifiModuleAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string firmwarePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool skipVersionCheck = false) => throw new NotSupportedException();

        public Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    #endregion
}
