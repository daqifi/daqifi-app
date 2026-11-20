using System.Collections.ObjectModel;
using System.Reflection;
using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Avalonia.Tests.Views;
using Daqifi.Core.Firmware;
using Daqifi.Desktop;
using Daqifi.Desktop.Device.Firmware;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
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
/// the module. Three tests spend the app's real two-second pause, so the class takes about eight
/// seconds.
/// </para>
///
/// <para>
/// The ticket has two callers. The flash path must announce each retry. The connect-time probe in
/// <see cref="DaqifiViewModel"/> uses the same retry policy, but it must not write the status line
/// (issue #241). The last test runs that probe, not only the shared policy (review of PR #652).
/// </para>
/// </summary>
/// <remarks>
/// In <see cref="ConnectionManagerSingletonCollection"/> because the connect-time probe reads its
/// devices from <c>ConnectionManager.Instance</c>, and the last test registers one there. The
/// coordinator tests use no process-wide state.
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class WifiChipInfoRetryStatusTests : IDisposable
{
    private const string Checking = "Checking WiFi firmware version...";

    private const string Waiting =
        "Waiting for device to finish starting up before checking WiFi firmware version";

    private readonly SerialTestUi _ui = new();
    private readonly RecordingHost _host = new();
    private readonly List<DesktopStreamingDevice> _registered = [];
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
        foreach (var device in _registered)
        {
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); }
            catch { /* best-effort cleanup */ }
        }

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
    /// The other caller. The connect-time probe runs when a device connects, not during an update,
    /// so it must not write the firmware status line (issue #241). That line can still show the
    /// last update's outcome. The test runs the real probe in <see cref="DaqifiViewModel"/> with a
    /// module that misses the first attempt. Core retries, so the module is asked a second time and
    /// its version reaches the device. The status line keeps its text the whole time. If this caller
    /// gave Core a progress sink that writes the line, as the flash path does, the write would occur
    /// between the two attempts and this test would fail.
    /// </summary>
    [Fact]
    public async Task The_connect_time_probe_retries_and_does_not_write_the_status_line()
    {
        const string LastUpdateOutcome = "Firmware update complete.";
        var device = ScriptedWincDevice.That(
            [null, Chip(FirmwareUpdateCoordinator.MinimumWifiFirmwareVersion)], connected: true);
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
        var shell = ShellReadyToProbe();
        shell.FirmwareUpdateStatusText = LastUpdateOutcome;
        var statusWrites = new List<string>();
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DaqifiViewModel.FirmwareUpdateStatusText))
            {
                statusWrites.Add(shell.FirmwareUpdateStatusText);
            }
        };

        await _ui.RunAsync(() => RunConnectTimeProbe(shell));

        Assert.Equal(2, device.Queries);
        Assert.Equal(FirmwareUpdateCoordinator.MinimumWifiFirmwareVersion, device.WifiFirmwareVersion);
        Assert.Empty(await _ui.Get(() => statusWrites.ToList()));
        Assert.Equal(LastUpdateOutcome, shell.FirmwareUpdateStatusText);
    }

    #region Helpers
    private static LanChipInfo Chip(string version) =>
        new() { ChipId = 0x1503A0, FwVersion = version, BuildDate = "2020-01-01" };

    /// <summary>
    /// The shell, with debug mode on and its UI-thread check answering "yes". Debug mode gates the
    /// probe. The test sets the field behind it, because the property's change handler clears the
    /// debug log through a blocking <c>Dispatcher.UIThread.Invoke</c>, and with no Avalonia
    /// application that call never returns. The answer "yes" is true: the probe runs on the
    /// <see cref="SerialTestUi"/> thread, which stands in for the UI thread. Neither service is
    /// reached: the probe downloads and flashes nothing.
    /// </summary>
    private static DaqifiViewModel ShellReadyToProbe()
    {
        var shell = new DaqifiViewModel(
            new NullDialogService(), new UnreachableUpdateService(), new UnreachableDownloadService());
        SetPrivateField(shell, "_isDebugModeEnabled", true);
        SetPrivateField(shell, "_checkUiThreadAccess", (Func<bool>)(() => true));
        return shell;
    }

    /// <summary>
    /// Starts the probe through the method that every trigger calls (a connect and debug mode on),
    /// and returns its task, so the test can wait until the probe has finished.
    /// </summary>
    private static Task RunConnectTimeProbe(DaqifiViewModel shell)
    {
        var probe = typeof(DaqifiViewModel).GetMethod(
            "CheckWifiFirmwareAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(probe);
        return (Task)probe.Invoke(shell, null)!;
    }

    private static void SetPrivateField(DaqifiViewModel shell, string fieldName, object value)
    {
        var field = typeof(DaqifiViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(shell, value);
    }
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
        private readonly bool _connected;

        private ScriptedWincDevice(LanChipInfo?[] answers, bool connected)
            : base("COM-TEST-308", "Nq1", "SN-0308", firmwareVersion: "3.9.0")
        {
            _answers = answers;
            _connected = connected;
            Metadata.Capabilities.HasWincWifiModule = true;
        }

        /// <summary>
        /// Answers in order; an attempt past the end of the script reads nothing. The connect-time
        /// probe skips a device that is not connected, so its test asks for
        /// <paramref name="connected"/>. The coordinator tests keep the default: with no Core
        /// device, <see cref="IsConnected"/> is false, as before.
        /// </summary>
        public static ScriptedWincDevice That(LanChipInfo?[] answers, bool connected = false) =>
            new(answers, connected);

        public override bool IsConnected => _connected;

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
