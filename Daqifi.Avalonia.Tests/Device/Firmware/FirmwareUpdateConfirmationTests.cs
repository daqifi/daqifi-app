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
/// Pins issue #564: one click on UPDATE in the device drawer started a firmware write, with no
/// prompt, and nothing told the user that the update can erase the device's WiFi settings.
/// <para>
/// The coordinator now asks the host to confirm after its guards and before anything touches the
/// device, and adds a "set the WiFi settings again" notification after a successful write. The
/// device here is the app's real <see cref="SerialStreamingDevice"/> over a connected Core device on
/// a <see cref="CapturingTransport"/>, so a confirmed run really reaches
/// <see cref="IFirmwareUpdateService.UpdateFirmwareAsync(Daqifi.Core.Device.IStreamingDevice,string,IProgress{FirmwareUpdateProgress}?,string?,string?,CancellationToken)"/>
/// — which is what makes "a cancel never reaches it" a fact rather than a run that stopped early
/// for some other reason. Every run is a manual (PIC32-only) upload, so no WiFi step is involved.
/// </para>
/// </summary>
public sealed class FirmwareUpdateConfirmationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly PromptHost _host = new();
    private readonly CountingUpdateService _updates = new();
    private readonly CountingDownloadService _downloads = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-firmware-confirm-tests", Guid.NewGuid().ToString("N"));
    private readonly CapturingTransport _transport = new();
    private readonly DaqifiStreamingDevice _core;
    private readonly SerialStreamingDevice _device;

    public FirmwareUpdateConfirmationTests()
    {
        _core = new DaqifiStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _device = new SerialStreamingDevice("COM-TEST-564", _core)
        {
            DeviceSerialNo = "SN564",
            DeviceVersion = "3.8.0",
            FriendlyName = "Bench Nq1",
        };
        _host.SelectedDevice = _device;
        _host.FirmwareFilePath = FirmwareFile();
    }

    public void Dispose()
    {
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private FirmwareUpdateCoordinator CreateCoordinator() =>
        new(_host,
            _updates,
            _downloads,
            NullLogger<FirmwareUpdateService>.Instance,
            new RecordingAppLogger(),
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: false);

    #region The write starts only after the user confirms
    /// <summary>
    /// The ticket's first verification: a cancel never reaches the write and the uploading flag never
    /// goes up. "Untouched" is asserted as broadly as the host can see it — no status text, no WiFi
    /// probe quiesce, no download, no notification, and the user's file selection kept for a retry.
    /// </summary>
    [Fact]
    public async Task A_cancel_leaves_the_device_untouched()
    {
        _host.Answer = false;
        var selectedFile = _host.FirmwareFilePath;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(1, _host.PromptCount);
        Assert.Equal(0, _updates.Pic32FlashCount);
        Assert.Equal(0, _downloads.LatestFirmwareDownloadCount);
        Assert.Equal(0, _host.QuiesceCount);
        Assert.False(_host.IsFirmwareUploading);
        Assert.False(_host.FlagWasEverRaised);
        Assert.Empty(_host.StatusWrites);
        Assert.Empty(_host.Notifications);
        Assert.Equal(0, _host.SucceededCount);
        Assert.Equal(selectedFile, _host.FirmwareFilePath);
    }

    /// <summary>The ticket's second verification: a confirm starts the write, once.</summary>
    [Fact]
    public async Task A_confirm_starts_the_write_once()
    {
        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(1, _host.PromptCount);
        Assert.Equal(1, _updates.Pic32FlashCount);
        Assert.True(_host.IsUploadComplete);
        Assert.Equal(1, _host.SucceededCount);
        Assert.False(_host.IsFirmwareUploading);
    }

    /// <summary>
    /// While the prompt is open nothing has started: the flag is down, so the drawer shows no Cancel
    /// for a run that does not exist, and the write waits for the answer.
    /// </summary>
    [Fact]
    public async Task Nothing_runs_while_the_prompt_is_open()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.PendingAnswer = answer.Task;

        var run = CreateCoordinator().UploadFirmwareAsync();

        Assert.False(run.IsCompleted);
        Assert.Equal(1, _host.PromptCount);
        Assert.False(_host.FlagWasEverRaised);
        Assert.Empty(_host.StatusWrites);
        Assert.Equal(0, _updates.Pic32FlashCount);

        answer.SetResult(true);
        await run.WaitAsync(Timeout);

        Assert.Equal(1, _updates.Pic32FlashCount);
    }

    /// <summary>
    /// The prompt is awaited, so the drawer can be pointed at another device under it. A "yes" given
    /// for one device must not write the device that happens to be selected when it arrives — nor
    /// the one it was given for, which the drawer no longer shows.
    /// </summary>
    [Fact]
    public async Task A_confirm_for_a_device_that_is_no_longer_selected_writes_nothing()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.PendingAnswer = answer.Task;

        var run = CreateCoordinator().UploadFirmwareAsync();
        _host.SelectedDevice = new SerialStreamingDevice("COM-TEST-OTHER");
        answer.SetResult(true);
        await run.WaitAsync(Timeout);

        Assert.Equal(0, _updates.Pic32FlashCount);
        Assert.False(_host.FlagWasEverRaised);
    }

    /// <summary>
    /// Another run that started while the prompt was open wins; the late "yes" adds nothing to it.
    /// </summary>
    [Fact]
    public async Task A_confirm_that_arrives_during_another_run_writes_nothing()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.PendingAnswer = answer.Task;

        var run = CreateCoordinator().UploadFirmwareAsync();
        _host.IsFirmwareUploading = true;
        answer.SetResult(true);
        await run.WaitAsync(Timeout);

        Assert.Equal(0, _updates.Pic32FlashCount);
    }

    /// <summary>
    /// The prompt comes after the coordinator's guards: a device the update cannot run on gets no
    /// prompt, because confirming would then do nothing.
    /// </summary>
    [Fact]
    public async Task A_device_the_update_cannot_run_on_gets_no_prompt()
    {
        _host.SelectedDevice = null;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(0, _host.PromptCount);
    }

    /// <summary>A run already in progress is not interrupted by a prompt for a second one.</summary>
    [Fact]
    public async Task A_run_in_progress_gets_no_second_prompt()
    {
        _host.IsFirmwareUploading = true;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(0, _host.PromptCount);
        Assert.Equal(0, _updates.Pic32FlashCount);
    }
    #endregion

    #region What the prompt says
    /// <summary>
    /// The ticket's list: the device name, the installed and new versions, the duration, the cable
    /// warning and the erase warning — read off what the coordinator actually handed the host.
    /// </summary>
    [Fact]
    public async Task The_prompt_names_the_device_the_versions_and_the_risks()
    {
        _host.Answer = false;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal("Update firmware?", _host.LastTitle);
        var message = _host.LastMessage;
        Assert.Contains("Device: Bench Nq1", message);
        Assert.Contains("Installed version: 3.8.0", message);
        Assert.Contains("New version: the file firmware.hex", message);
        Assert.Contains("approximately 1 minute", message);
        Assert.Contains("Do not disconnect the USB cable", message);
        Assert.Contains("can erase the WiFi settings and the calibration", message);
    }

    /// <summary>An auto-update names the release the last version check found.</summary>
    [Fact]
    public void An_auto_update_names_the_latest_release()
    {
        var message = FirmwareUpdateCoordinator.DescribeFirmwareUpdate(_device, "3.9.0", manualFirmwarePath: string.Empty);

        Assert.Contains("Installed version: 3.8.0", message);
        Assert.Contains("New version: 3.9.0", message);
    }

    /// <summary>
    /// Before any version check succeeds, and for a device that has not reported its version, the
    /// prompt says so in words rather than printing an empty field.
    /// </summary>
    [Fact]
    public void Unknown_versions_are_named_rather_than_left_blank()
    {
        var device = new SerialStreamingDevice("COM-TEST-564-BLANK");

        var message = FirmwareUpdateCoordinator.DescribeFirmwareUpdate(device, string.Empty, string.Empty);

        Assert.Contains("Installed version: unknown", message);
        Assert.Contains("New version: the latest release", message);
    }
    #endregion

    #region The notification after the write
    /// <summary>
    /// A successful write leaves a notification, owned by the device, that tells the user to set the
    /// WiFi settings again — the success dialog closes and the status line hides when the run ends.
    /// </summary>
    [Fact]
    public async Task A_successful_write_tells_the_user_to_set_the_wifi_settings_again()
    {
        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        var notification = Assert.Single(_host.Notifications);
        Assert.Equal("SN564", notification.DeviceSerialNo);
        Assert.StartsWith("Bench Nq1:", notification.Message);
        Assert.Contains("Set the WiFi settings of the device again.", notification.Message);
        Assert.False(notification.IsFirmwareUpdate);
        Assert.Equal(1, _host.RefreshCount);
    }

    /// <summary>A failed write changed nothing the user must redo, so it adds no such notification.</summary>
    [Fact]
    public async Task A_failed_write_adds_no_wifi_notification()
    {
        _updates.Fail = true;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(1, _updates.Pic32FlashCount);
        Assert.Empty(_host.Notifications);
    }

    /// <summary>Two updates of one device leave one reminder, not two.</summary>
    [Fact]
    public async Task A_second_update_does_not_stack_a_second_notification()
    {
        var coordinator = CreateCoordinator();

        await coordinator.UploadFirmwareAsync().WaitAsync(Timeout);
        _host.FirmwareFilePath = FirmwareFile();
        await coordinator.UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Equal(2, _updates.Pic32FlashCount);
        Assert.Single(_host.Notifications);
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

    /// <summary>Host that answers the prompt as told and records what the run did to it.</summary>
    private sealed class PromptHost : IFirmwareUpdateHost
    {
        private bool _isFirmwareUploading;

        /// <summary>The answer given at once, used when <see cref="PendingAnswer"/> is null.</summary>
        public bool Answer { get; set; } = true;

        /// <summary>An answer the test resolves later, to hold the prompt open.</summary>
        public Task<bool>? PendingAnswer { get; set; }

        public int PromptCount { get; private set; }

        public string LastTitle { get; private set; } = string.Empty;

        public string LastMessage { get; private set; } = string.Empty;

        public bool FlagWasEverRaised { get; private set; }

        public int QuiesceCount { get; private set; }

        public int SucceededCount { get; private set; }

        public int RefreshCount { get; private set; }

        public List<string> StatusWrites { get; } = [];

        public DesktopStreamingDevice? SelectedDevice { get; set; }

        public IReadOnlyList<DesktopStreamingDevice> ConnectedDevices { get; } = [];

        public string FirmwareFilePath { get; set; } = string.Empty;

        public bool SelectedDeviceSupportsFirmwareUpdate { get; set; }

        public bool IsFirmwareUploading
        {
            get => _isFirmwareUploading;
            set
            {
                _isFirmwareUploading = value;
                FlagWasEverRaised |= value;
            }
        }

        public bool IsUploadComplete { get; set; }

        public bool HasErrorOccured { get; set; }

        public int UploadFirmwareProgress { get; set; }

        public int UploadWiFiProgress { get; set; }

        public string FirmwareUpdateStatusText
        {
            set => StatusWrites.Add(value);
        }

        public ObservableCollection<Notifications> Notifications { get; } = [];

        public DesktopStreamingDevice? DeviceBeingUpdated { get; set; }

        public void RefreshNotificationCount() => RefreshCount++;

        public void ShowFirmwareError(string message) { }

        public void ShowFirmwareUpdateSucceeded() => SucceededCount++;

        public Task<bool> ConfirmFirmwareUpdateAsync(string title, string message)
        {
            PromptCount++;
            LastTitle = title;
            LastMessage = message;
            return PendingAnswer ?? Task.FromResult(Answer);
        }

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default)
        {
            QuiesceCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>PIC32 update service that counts writes and can fail one.</summary>
    private sealed class CountingUpdateService : IFirmwareUpdateService
    {
        public int Pic32FlashCount { get; private set; }

        public bool Fail { get; set; }

        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

#pragma warning disable CS0067 // Part of the interface; nothing in these tests raises it.
        public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) => Flash();

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress,
            string? targetDevicePath,
            string? targetLocationKey,
            CancellationToken cancellationToken = default) => Flash();

        public Task UpdateWifiModuleAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string firmwarePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool skipVersionCheck = false) => throw new NotSupportedException("A manual upload has no WiFi step.");

        public Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private Task Flash()
        {
            Pic32FlashCount++;
            return Fail
                ? Task.FromException(new InvalidOperationException("simulated flash failure"))
                : Task.CompletedTask;
        }
    }

    /// <summary>Download service that counts the auto-update download and is otherwise unused.</summary>
    private sealed class CountingDownloadService : IFirmwareDownloadService
    {
        public int LatestFirmwareDownloadCount { get; private set; }

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LatestFirmwareDownloadCount++;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InvalidateCache() { }
    }
    #endregion
}
