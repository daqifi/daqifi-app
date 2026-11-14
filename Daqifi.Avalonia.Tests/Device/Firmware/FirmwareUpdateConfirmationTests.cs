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
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-firmware-confirm-tests", Guid.NewGuid().ToString("N"));
    private readonly CountingDownloadService _downloads;
    private readonly CapturingTransport _transport = new();
    private readonly DaqifiStreamingDevice _core;
    private readonly SerialStreamingDevice _device;

    public FirmwareUpdateConfirmationTests()
    {
        _downloads = new CountingDownloadService(Path.Combine(_dataDirectory, "downloads"));
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
        Assert.Equal(0, _downloads.DownloadCount);
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

    /// <summary>An auto-update names the release resolved for it.</summary>
    [Fact]
    public void An_auto_update_names_the_resolved_release()
    {
        var message = FirmwareUpdateCoordinator.DescribeFirmwareUpdate(_device, "3.9.0", manualFirmwarePath: string.Empty);

        Assert.Contains("Installed version: 3.8.0", message);
        Assert.Contains("New version: 3.9.0", message);
    }

    /// <summary>
    /// A device that has not reported its version gets "unknown" in words rather than an empty field.
    /// </summary>
    [Fact]
    public void An_unknown_installed_version_is_named_rather_than_left_blank()
    {
        var device = new SerialStreamingDevice("COM-TEST-564-BLANK");

        var message = FirmwareUpdateCoordinator.DescribeFirmwareUpdate(device, "3.9.0", string.Empty);

        Assert.Contains("Installed version: unknown", message);
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

    #region The write is the firmware the prompt named
    /// <summary>
    /// Review finding 1 on PR #575, automatic path: release A is the latest when the prompt opens and
    /// release B is published before the user answers. The write must be A — the version the user
    /// agreed to — downloaded by its tag, never "whatever is latest now".
    /// </summary>
    [Fact]
    public async Task An_auto_update_writes_the_release_the_prompt_named_even_if_a_newer_one_appears()
    {
        _host.FirmwareFilePath = string.Empty;
        _downloads.Releases.Add(Release("3.9.0"));
        _downloads.Releases.Add(Release("3.9.1"));

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        Assert.Contains("New version: 3.9.0", _host.LastMessage);
        Assert.Equal(["v3.9.0"], _downloads.DownloadedTags);
        Assert.Equal(0, _downloads.LatestFirmwareDownloadCount);
        Assert.Equal([_downloads.PathFor("v3.9.0")], _updates.FlashedPaths);
    }

    /// <summary>
    /// Review finding 1, manual path: the path is read once, before the prompt. A path that changes
    /// while the prompt is open does not redirect the write to a file the prompt never named.
    /// </summary>
    [Fact]
    public async Task A_manual_update_writes_the_file_the_prompt_named_even_if_the_path_changes()
    {
        var namedFile = _host.FirmwareFilePath;
        var otherFile = Path.Combine(_dataDirectory, "other.hex");
        File.WriteAllText(otherFile, ":00000001FF\n");
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.PendingAnswer = answer.Task;

        var run = CreateCoordinator().UploadFirmwareAsync();
        _host.FirmwareFilePath = otherFile;
        answer.SetResult(true);
        await run.WaitAsync(Timeout);

        Assert.Contains("New version: the file firmware.hex", _host.LastMessage);
        Assert.Equal([namedFile], _updates.FlashedPaths);
    }

    /// <summary>
    /// Review finding 1, no release: a prompt that cannot name the version must not start a write. No
    /// prompt opens, nothing is written, the pane is not left "uploading", and the user is told why.
    /// </summary>
    [Fact]
    public async Task No_release_means_no_prompt_and_no_write()
    {
        _host.FirmwareFilePath = string.Empty;

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        AssertStoppedBeforeThePrompt();
        Assert.Equal([FirmwareUpdateCoordinator.ReleaseNotFoundMessage], _host.Errors);
    }

    /// <summary>The same when the lookup itself fails, which is what no internet connection looks like.</summary>
    [Fact]
    public async Task A_failed_release_lookup_means_no_prompt_and_no_write()
    {
        _host.FirmwareFilePath = string.Empty;
        _downloads.LookupFailure = new HttpRequestException("no route to host");

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        AssertStoppedBeforeThePrompt();
        Assert.Equal([FirmwareUpdateCoordinator.ReleaseNotFoundMessage], _host.Errors);
    }

    /// <summary>
    /// A selected file that is gone is found before the prompt, so the user is not asked to confirm a
    /// write that would then fail.
    /// </summary>
    [Fact]
    public async Task A_missing_manual_file_means_no_prompt_and_no_write()
    {
        _host.FirmwareFilePath = Path.Combine(_dataDirectory, "deleted.hex");

        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        AssertStoppedBeforeThePrompt();
        var error = Assert.Single(_host.Errors);
        Assert.Contains("deleted.hex", error);
    }

    private void AssertStoppedBeforeThePrompt()
    {
        Assert.Equal(0, _host.PromptCount);
        Assert.Equal(0, _updates.Pic32FlashCount);
        Assert.Equal(0, _downloads.DownloadCount);
        Assert.False(_host.FlagWasEverRaised);
        Assert.False(_host.IsFirmwareUploading);
        Assert.Empty(_host.StatusWrites);
    }
    #endregion

    #region The success dialog
    /// <summary>
    /// Review finding 2 on PR #575: the success dialog itself tells the user to set the WiFi settings
    /// again, in the same words as the notification, after the completion text.
    /// </summary>
    [Fact]
    public async Task The_success_dialog_tells_the_user_to_set_the_wifi_settings_again()
    {
        await CreateCoordinator().UploadFirmwareAsync().WaitAsync(Timeout);

        var message = Assert.Single(_host.SuccessMessages);
        Assert.Equal(
            "Firmware update completed successfully.\n\n" + FirmwareUpdateCoordinator.WifiSettingsMayBeErasedInstruction,
            message);
        Assert.EndsWith(FirmwareUpdateCoordinator.WifiSettingsMayBeErasedInstruction, Assert.Single(_host.Notifications).Message);
    }
    #endregion

    #region Helpers
    private static FirmwareReleaseInfo Release(string version)
    {
        Assert.True(FirmwareVersion.TryParse(version, out var parsed));
        return new FirmwareReleaseInfo { Version = parsed, TagName = $"v{version}", IsPreRelease = false };
    }

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

        public void ShowFirmwareError(string message) => Errors.Add(message);

        public List<string> SuccessMessages { get; } = [];

        public List<string> Errors { get; } = [];

        public int SucceededCount => SuccessMessages.Count;

        public void ShowFirmwareUpdateSucceeded(string message) => SuccessMessages.Add(message);

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
        public int Pic32FlashCount => FlashedPaths.Count;

        public List<string> FlashedPaths { get; } = [];

        public bool Fail { get; set; }

        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

#pragma warning disable CS0067 // Part of the interface; nothing in these tests raises it.
        public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) => Flash(hexFilePath);

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress,
            string? targetDevicePath,
            string? targetLocationKey,
            CancellationToken cancellationToken = default) => Flash(hexFilePath);

        public Task UpdateWifiModuleAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string firmwarePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool skipVersionCheck = false) => throw new NotSupportedException("These runs never flash the WiFi module.");

        public Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private Task Flash(string hexFilePath)
        {
            FlashedPaths.Add(hexFilePath);
            return Fail
                ? Task.FromException(new InvalidOperationException("simulated flash failure"))
                : Task.CompletedTask;
        }
    }

    /// <summary>
    /// Download service with a scripted release feed. <see cref="Releases"/> is what successive
    /// <c>GetLatestReleaseAsync</c> calls return, the last entry repeating, and what
    /// <c>DownloadLatestFirmwareAsync</c> treats as "latest now" — so "A, then B" models a release
    /// published while the prompt is open. Both download methods write a stand-in file named after the
    /// release they fetched, so the path the flash receives says which release it was.
    /// </summary>
    private sealed class CountingDownloadService(string directory) : IFirmwareDownloadService
    {
        private int _lookups;

        public List<FirmwareReleaseInfo?> Releases { get; } = [];

        public Exception? LookupFailure { get; set; }

        public int LatestFirmwareDownloadCount { get; private set; }

        public List<string> DownloadedTags { get; } = [];

        public int DownloadCount => LatestFirmwareDownloadCount + DownloadedTags.Count;

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
            var latest = Releases.Count == 0 ? null : Releases[^1];
            return Task.FromResult(latest == null ? null : WriteFile(latest.TagName));
        }

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadedTags.Add(tagName);
            return Task.FromResult<string?>(WriteFile(tagName));
        }

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default)
        {
            if (LookupFailure != null)
            {
                return Task.FromException<FirmwareReleaseInfo?>(LookupFailure);
            }

            if (Releases.Count == 0)
            {
                return Task.FromResult<FirmwareReleaseInfo?>(null);
            }

            var release = Releases[Math.Min(_lookups, Releases.Count - 1)];
            _lookups++;
            return Task.FromResult(release);
        }

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InvalidateCache() { }

        /// <summary>The path a download of <paramref name="tagName"/> lands at.</summary>
        public string PathFor(string tagName) => Path.Combine(directory, $"release-{tagName}.hex");

        private string WriteFile(string tagName)
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(tagName);
            File.WriteAllText(path, ":00000001FF\n");
            return path;
        }
    }
    #endregion
}
