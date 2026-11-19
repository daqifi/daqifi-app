using System.Collections.ObjectModel;
using Daqifi.Core.Communication.Producers;
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
/// Pins what reaches the board's serial transport during a WiFi-module flash, and when, relative
/// to the WINC flash tool (review of PR #582).
/// </summary>
/// <remarks>
/// <para>
/// Core 1.10.0 enters the USB-to-WINC bridge in its prep step: <c>POWer:STATe 1</c>,
/// <c>LAN:FWUpdate</c>, then <c>LAN:APPLY</c> (daqifi-core#877). It sends them through the device
/// the app gives it. An earlier revision gave it a <see cref="BootloaderSessionStreamingDeviceAdapter"/>,
/// whose <c>Send</c> does nothing, so the APPLY that enters the bridge never reached the board and
/// the tool found no bridge. A test that reads only the options cannot see that; these tests read
/// the wire.
/// </para>
/// <para>
/// The device is a real Core <see cref="DaqifiStreamingDevice"/> over a
/// <see cref="CapturingTransport"/>, wrapped by the app's own <see cref="SerialStreamingDevice"/>, and
/// the flash tool is a fake runner that records the wire at the moment it starts. Everything in
/// between is the coordinator's and Core's production code.
/// </para>
/// </remarks>
public sealed class WifiBridgeEntryTransportTests : IDisposable
{
    private const string FlashSucceeded = "Operation completed successfully";

    private readonly CapturingTransport _transport = new();
    private readonly DaqifiStreamingDevice _coreDevice;
    private readonly SerialStreamingDevice _device;
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "daqifi-wifi-bridge-entry-tests", Guid.NewGuid().ToString("N"));

    public WifiBridgeEntryTransportTests()
    {
        _coreDevice = new DaqifiStreamingDevice("core", _transport, NullLogger.Instance);
        _coreDevice.Connect();
        _device = new SerialStreamingDevice("COM-TEST-582", _coreDevice);
        _device.Metadata.Capabilities.HasWincWifiModule = true;
    }

    public void Dispose()
    {
        _coreDevice.Dispose();
        _transport.Dispose();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The finding itself: the APPLY that enters the bridge is on the wire before the tool starts,
    /// after the request it acts on, and the port has been let go so the tool can open it.
    /// </summary>
    [Fact]
    public async Task The_bridge_entry_apply_is_written_before_the_flash_tool_starts()
    {
        var tool = new WireRecordingToolRunner(_transport, FlashSucceeded);

        await CreateCoordinator(tool).UpdateWifiModuleOnlyAsync(_device);

        Assert.Equal(1, tool.Runs);
        var wire = tool.WireAtStart!;
        var powerOn = wire.IndexOf(ScpiMessageProducer.TurnDeviceOn.Data, StringComparison.Ordinal);
        var request = wire.IndexOf(ScpiMessageProducer.SetLanFirmwareUpdateMode.Data, StringComparison.Ordinal);
        var apply = wire.IndexOf(ScpiMessageProducer.ApplyNetworkLan.Data, StringComparison.Ordinal);

        Assert.True(powerOn >= 0, $"POWer:STATe 1 was not written before the tool started. Wire: {wire}");
        Assert.True(request > powerOn, $"LAN:FWUpdate was not written after POWer:STATe 1. Wire: {wire}");
        Assert.True(apply > request, $"LAN:APPLY was not written after LAN:FWUpdate. Wire: {wire}");
        Assert.False(tool.ConnectedAtStart, "The device still held the port when the tool started.");
    }

    /// <summary>
    /// The app has already decided to flash, so Core must not ask the module its version again: on
    /// a forced flash that check could skip the flash the user asked for. The device the app passes
    /// now answers chip-info queries, so this is a real risk and not a theoretical one.
    /// </summary>
    [Fact]
    public async Task Core_does_not_ask_the_module_its_version_before_the_flash()
    {
        var tool = new WireRecordingToolRunner(_transport, FlashSucceeded);

        await CreateCoordinator(tool).UpdateWifiModuleOnlyAsync(_device);

        Assert.DoesNotContain(ScpiMessageProducer.GetLanChipInfo.Data, tool.WireAtStart!, StringComparison.Ordinal);
    }

    /// <summary>
    /// After the tool, Core reconnects the same device and takes the board out of the bridge, then
    /// restores the LAN settings. Before this fix the app disconnected the device itself and Core
    /// "reconnected" the no-op adapter, so none of this reached the board through Core.
    /// </summary>
    [Fact]
    public async Task After_the_flash_the_device_leaves_the_bridge_and_restores_the_lan()
    {
        var tool = new WireRecordingToolRunner(_transport, FlashSucceeded);

        await CreateCoordinator(tool).UpdateWifiModuleOnlyAsync(_device);

        var afterTool = _transport.SentText[tool.WireAtStart!.Length..];
        var exit = afterTool.IndexOf(ScpiMessageProducer.SetUsbTransparencyMode(0).Data, StringComparison.Ordinal);
        var apply = afterTool.IndexOf(ScpiMessageProducer.ApplyNetworkLan.Data, StringComparison.Ordinal);
        var save = afterTool.IndexOf(ScpiMessageProducer.SaveNetworkLan.Data, StringComparison.Ordinal);

        Assert.True(exit >= 0, $"The transparent-mode exit was not written after the tool. Wire after the tool: {afterTool}");
        Assert.True(apply > exit, $"LAN:APPLY was not written after the exit. Wire after the tool: {afterTool}");
        Assert.True(save > apply, $"LAN:SAVE was not written after LAN:APPLY. Wire after the tool: {afterTool}");
        Assert.True(_device.IsConnected, "The device was not reconnected after the flash.");
    }

    /// <summary>
    /// A flash the tool did not finish is a failure, and Core's recovery takes the board out of the
    /// bridge through the same device.
    /// </summary>
    [Fact]
    public async Task A_failed_flash_leaves_the_bridge_through_the_device()
    {
        var tool = new WireRecordingToolRunner(_transport, "Programming failed");

        await Assert.ThrowsAnyAsync<Exception>(() => CreateCoordinator(tool).UpdateWifiModuleOnlyAsync(_device));

        var afterTool = _transport.SentText[tool.WireAtStart!.Length..];
        var exit = afterTool.IndexOf(ScpiMessageProducer.SetUsbTransparencyMode(0).Data, StringComparison.Ordinal);
        var apply = afterTool.IndexOf(ScpiMessageProducer.ApplyNetworkLan.Data, StringComparison.Ordinal);

        Assert.True(exit >= 0, $"The transparent-mode exit was not written after the tool. Wire after the tool: {afterTool}");
        Assert.True(apply > exit, $"LAN:APPLY was not written after the exit. Wire after the tool: {afterTool}");
    }

    private FirmwareUpdateCoordinator CreateCoordinator(IExternalProcessRunner tool) =>
        new(new SilentHost(),
            new UnusedPic32UpdateService(),
            new LocalWifiPackage(CreateWifiPackage()),
            NullLogger<FirmwareUpdateService>.Instance,
            new RecordingAppLogger(),
            _dataDirectory,
            wifiUpdateModeSettleDelay: TimeSpan.Zero,
            canFlashWifiModule: true,
            wifiFlashToolRunner: tool);

    /// <summary>The layout of the WiFi package the app downloads: the tool under <c>winc/</c>.</summary>
    private string CreateWifiPackage()
    {
        var extracted = Path.Combine(_dataDirectory, "wifi-firmware-19.7.7");
        Directory.CreateDirectory(Path.Combine(extracted, "winc"));
        File.WriteAllText(Path.Combine(extracted, "winc", "winc_flash_tool.cmd"), "@echo off\n");
        return extracted;
    }

    #region Fakes
    /// <summary>
    /// Stands in for <c>winc_flash_tool.cmd</c>. Records what had reached the device's transport,
    /// and whether the device still held it, at the moment Core started the tool.
    /// </summary>
    private sealed class WireRecordingToolRunner(CapturingTransport transport, string lastLine) : IExternalProcessRunner
    {
        public int Runs { get; private set; }

        public string? WireAtStart { get; private set; }

        public bool? ConnectedAtStart { get; private set; }

        public Task<ExternalProcessResult> RunAsync(
            ExternalProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Runs++;
            WireAtStart ??= transport.SentText;
            ConnectedAtStart ??= transport.IsConnected;
            return Task.FromResult(new ExternalProcessResult(
                exitCode: 0,
                timedOut: false,
                duration: TimeSpan.FromMinutes(1),
                standardOutputLines: ["verify passed", lastLine],
                standardErrorLines: []));
        }
    }

    /// <summary>Hands the coordinator a WiFi package that is already on disk.</summary>
    private sealed class LocalWifiPackage(string extractedPath) : IFirmwareDownloadService
    {
        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(string, string)?>((extractedPath, "19.7.7"));

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

    /// <summary>The PIC32 update service, which a WiFi-only flash never reaches.</summary>
    private sealed class UnusedPic32UpdateService : IFirmwareUpdateService
    {
        public FirmwareUpdateState CurrentState => FirmwareUpdateState.Idle;

#pragma warning disable CS0067 // Part of the interface; nothing in these tests raises it.
        public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public Task UpdateFirmwareAsync(
            IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateFirmwareAsync(
            IStreamingDevice device,
            string hexFilePath,
            IProgress<FirmwareUpdateProgress>? progress,
            string? targetDevicePath,
            string? targetLocationKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateWifiModuleAsync(
            IStreamingDevice device,
            string firmwarePath,
            IProgress<FirmwareUpdateProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool skipVersionCheck = false) => throw new NotSupportedException();

        public Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
            IStreamingDevice device,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>A host that accepts every write and shows nothing.</summary>
    private sealed class SilentHost : IFirmwareUpdateHost
    {
        public DesktopStreamingDevice? SelectedDevice { get; set; }

        public IReadOnlyList<DesktopStreamingDevice> ConnectedDevices { get; } = [];

        public string FirmwareFilePath { get; set; } = string.Empty;

        public bool SelectedDeviceSupportsFirmwareUpdate { get; set; }

        public bool IsFirmwareUploading { get; set; }

        public bool IsUploadComplete { get; set; }

        public bool HasErrorOccured { get; set; }

        public int UploadFirmwareProgress { get; set; }

        public int UploadWiFiProgress { get; set; }

        public string FirmwareUpdateStatusText { set { } }

        public ObservableCollection<Notifications> Notifications { get; } = [];

        public DesktopStreamingDevice? DeviceBeingUpdated { get; set; }

        public void RefreshNotificationCount() { }

        public void ShowFirmwareError(string message) { }

        public void ShowFirmwareUpdateSucceeded(string message) { }

        public Task<bool> ConfirmFirmwareUpdateAsync(string title, string message, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task QuiesceWifiFirmwareProbeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    #endregion
}
