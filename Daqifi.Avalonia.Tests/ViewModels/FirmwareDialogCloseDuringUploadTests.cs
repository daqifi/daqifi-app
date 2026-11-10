using Daqifi.Core.Firmware;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #539: the Firmware Settings window could close during an upload, and the upload went on
/// with no progress, no cancel and no result. The window now asks
/// <see cref="FirmwareDialogViewModel.RefuseCloseDuringUpload"/> before it closes. These facts pin the
/// answer for each phase of an upload, with a download and a write that never complete on their own,
/// so no test reaches a real device.
///
/// <para>
/// The window half — that <c>FirmwareDialog.OnClosing</c> really cancels the close — needs a real
/// window and is gated by the capture harness screen <c>dialog-firmware-close-refused</c>, which calls
/// <c>Close()</c> on an uploading dialog and fails if the window goes away.
/// </para>
/// </summary>
public sealed class FirmwareDialogCloseDuringUploadTests : IDisposable
{
    private readonly ParkedDownloadService _downloads = new();
    private readonly ParkedUpdateService _updates = new();
    private readonly string _hexFile = Path.Combine(Path.GetTempPath(), $"daqifi-539-{Guid.NewGuid():N}.hex");

    public FirmwareDialogCloseDuringUploadTests()
    {
        File.WriteAllText(_hexFile, ":00000001FF\n");
    }

    public void Dispose()
    {
        File.Delete(_hexFile);
    }

    private FirmwareDialogViewModel CreateViewModel() =>
        new(hidDeviceName: "DAQiFi Bootloader",
            firmwareDownloadService: _downloads,
            firmwareUpdateService: _updates);

    [Fact]
    public void An_idle_window_may_close()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.RefuseCloseDuringUpload());
        Assert.False(viewModel.CloseRefusedDuringUpload);
    }

    /// <summary>Result B in the issue: the close arrives while the firmware is being written.</summary>
    [Fact]
    public async Task A_close_during_the_write_is_refused_and_the_reason_is_shown()
    {
        var viewModel = CreateViewModel();
        viewModel.FirmwareFilePath = _hexFile;

        var upload = viewModel.UploadFirmwareCommand.ExecuteAsync(null);
        await _updates.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(viewModel.RefuseCloseDuringUpload());
        Assert.True(viewModel.CloseRefusedDuringUpload);

        viewModel.CancelUploadFirmwareCommand.Execute(null);
        await upload.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(viewModel.IsFirmwareUploading);
        Assert.False(viewModel.RefuseCloseDuringUpload());
    }

    /// <summary>
    /// Result A in the issue: the close arrives while the firmware file downloads, before the write
    /// lease exists. This is the gap that let discovery restart under the write that followed.
    /// </summary>
    [Fact]
    public async Task A_close_during_the_download_is_refused()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedFirmware = new FirmwareOption { DeviceModel = "DAQiFi", Version = "3.8.0" };

        var upload = viewModel.UploadFirmwareCommand.ExecuteAsync(null);
        await _downloads.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(viewModel.RefuseCloseDuringUpload());

        viewModel.CancelUploadFirmwareCommand.Execute(null);
        await upload.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(_updates.Started.Task.IsCompleted, "The write must not start after a cancelled download.");
        Assert.False(viewModel.RefuseCloseDuringUpload());
    }

    /// <summary>The reason belongs to the upload that refused the close, not to the next one.</summary>
    [Fact]
    public async Task A_new_upload_starts_without_the_previous_reason()
    {
        var viewModel = CreateViewModel();
        viewModel.FirmwareFilePath = _hexFile;

        var first = viewModel.UploadFirmwareCommand.ExecuteAsync(null);
        await _updates.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        viewModel.RefuseCloseDuringUpload();
        viewModel.CancelUploadFirmwareCommand.Execute(null);
        await first.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(viewModel.CloseRefusedDuringUpload);

        _updates.Reset();
        var second = viewModel.UploadFirmwareCommand.ExecuteAsync(null);
        await _updates.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(viewModel.CloseRefusedDuringUpload);

        viewModel.CancelUploadFirmwareCommand.Execute(null);
        await second.WaitAsync(TimeSpan.FromSeconds(30));
    }

    #region Fakes
    /// <summary>A firmware write that runs until it is cancelled.</summary>
    private sealed class ParkedUpdateService : IFirmwareUpdateService
    {
        public TaskCompletionSource Started { get; private set; } = NewSignal();

        public void Reset() => Started = NewSignal();

        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

#pragma warning disable CS0067 // Part of the interface; nothing in these tests raises it.
        public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) => Park(cancellationToken);

        public Task UpdateFirmwareAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress,
            string? targetDevicePath,
            string? targetLocationKey,
            CancellationToken cancellationToken = default) => Park(cancellationToken);

        public Task UpdateWifiModuleAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            string firmwarePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool skipVersionCheck = false) => throw new NotSupportedException();

        public Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
            Daqifi.Core.Device.IStreamingDevice device,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private Task Park(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>A firmware download that runs until it is cancelled. The release feed reports nothing.</summary>
    private sealed class ParkedDownloadService : IFirmwareDownloadService
    {
        public TaskCompletionSource Started { get; } = NewSignal();

        public async Task<string?> DownloadLatestFirmwareAsync(
            string destinationDirectory,
            bool includePreRelease = false,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> DownloadFirmwareByTagAsync(
            string tagName,
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InvalidateCache() { }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    #endregion
}
