using System.ComponentModel;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Core.Firmware;
using Daqifi.Desktop;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Xunit;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #525: a device that dropped mid-session left the app showing LOGGING ON with the toggle
/// disabled, and the session never closed.
///
/// <para><c>ConnectionManager.TearDownDroppedDevice</c> releases the dropped device's channels —
/// deliberately, and that part is correct — then disconnects it and raises the user notification.
/// Nothing stopped the session. <c>DaqifiViewModel</c>'s notification handler then recomputed
/// <c>CanToggleLogging = ActiveChannels.Count > 0</c>, now false because the channels had just been
/// released, so the LOGGING ON label stayed lit over a toggle that could no longer be clicked:
/// <c>LoggingManager.Active</c> stayed true, the session row was never finalised, and the only ways
/// out were to reconnect the device AND re-enable a channel, or to restart the app.</para>
///
/// <para>These tests pin both halves of the decision that fixed it. The LAST device dropping closes
/// the run the same way the user's own toggle does; a device dropping out of a fleet that is still
/// streaming does not, because taking the run away from the devices still recording would lose data
/// the user asked for. The HeadlessBench <c>--scripted drop-mid-stream</c> row covers the first
/// half end to end through the real <c>ConnectionManager</c> teardown; the second half is only
/// reachable here, because every scripted state connects a single device.</para>
/// </summary>
/// <remarks>
/// <para>In <see cref="ConnectionManagerSingletonCollection"/> because the condition under test
/// reads <c>ConnectionManager.Instance.ConnectedDevices</c> and these tests write it. The same
/// collection is where <c>ChannelsPaneRebuildTests</c> lives, which also stands an app host up —
/// <c>App.InitializeMobile()</c> is what gives <c>LoggingManager.Instance</c> the
/// <c>IDbContextFactory</c> the <see cref="DaqifiViewModel.IsLogging"/> setter writes through, and
/// it points at the throwaway data directory the assembly's module initializer already set
/// <c>DAQIFI_DATA_DIR</c> to. Both that collection and <see cref="AppHostCollection"/> disable
/// parallelisation, so the two never overlap.</para>
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class DroppedDeviceSessionStopTests : IDisposable
{
    private readonly DaqifiViewModel _viewModel;
    private readonly List<IStreamingDevice> _registered = [];

    public DroppedDeviceSessionStopTests()
    {
        Daqifi.Desktop.App.InitializeMobile();

        // An offline download service, so the fire-and-forget firmware refresh at the end of every
        // UpdateUi pass cannot reach GitHub. It only runs at all when a device is left connected,
        // which is exactly the multi-device case below.
        _viewModel = new DaqifiViewModel(new NullDialogService(), firmwareDownloadService: new OfflineDownloadService());
    }

    public void Dispose()
    {
        // LoggingManager and ConnectionManager are both process-wide singletons, so anything left
        // behind here is handed to the next test in this collection.
        try { LoggingManager.Instance.Active = false; } catch { /* best-effort cleanup */ }
        foreach (var device in _registered)
        {
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); }
            catch { /* best-effort cleanup */ }
        }
        ConnectionManager.Instance.NotifyConnection = false;
        ConnectionManager.Instance.LastDisconnectReason = string.Empty;
    }

    /// <summary>
    /// The state a user is in when the cable is pulled: a device connected, a session running over
    /// it. Registered with the manager (the authoritative list the stop condition reads) and added
    /// to the view model's own fleet (the list the session is fanned out over), which is how a
    /// connect leaves the two.
    /// </summary>
    private RecordingStreamingDevice AConnectedDevice(string serial)
    {
        var device = new RecordingStreamingDevice(serial);
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
        _viewModel.ConnectedDevices.Add(device);
        return device;
    }

    /// <summary>
    /// What <c>ConnectionManager.TearDownDroppedDevice</c> leaves for the shell to find: the device
    /// gone from both lists and the notification raised with a reason that names it. The teardown's
    /// own order — release, disconnect, then notify — is why the device is already out of the lists
    /// by the time the handler runs.
    /// </summary>
    /// <remarks>
    /// The handler is called directly rather than through the manager's <c>PropertyChanged</c>:
    /// <c>DaqifiViewModel</c> only subscribes to that singleton when <c>App.IsStarted</c>, which
    /// the desktop <c>App.Initialize</c> sets and <c>InitializeMobile</c> does not.
    /// </remarks>
    private void DropDevice(IStreamingDevice device)
    {
        ConnectionManager.Instance.UnregisterConnectedDevice(device);
        _registered.Remove(device);
        _viewModel.ConnectedDevices.Remove(device);

        ConnectionManager.Instance.LastDisconnectReason = $"{device.Name} disconnected (connection lost).";
        ConnectionManager.Instance.NotifyConnection = true;
        _viewModel.UpdateUi(
            ConnectionManager.Instance,
            new PropertyChangedEventArgs(nameof(ConnectionManager.NotifyConnection)));
    }

    /// <summary>
    /// The report itself. One device, one session, the link goes — and the run is over, so the app
    /// closes it: toggle OFF and <c>LoggingManager.Active</c> down, which is what finalises the
    /// session row. Before the fix both read true with nothing connected and no channel subscribed.
    /// </summary>
    [Fact]
    public void WhenTheLastDeviceDrops_TheSessionIsClosed()
    {
        var device = AConnectedDevice("Nq1-ONLY");

        _viewModel.IsLogging = true;
        Assert.True(_viewModel.IsLogging);
        Assert.True(LoggingManager.Instance.Active);

        DropDevice(device);

        Assert.False(_viewModel.IsLogging);
        Assert.False(LoggingManager.Instance.Active);
    }

    /// <summary>
    /// The half that makes the condition worth having. Two devices streaming, one drops: the run
    /// belongs to the device still recording, so the session must survive. Stopping on every drop
    /// would silently end a multi-device run the moment one link flickered.
    /// </summary>
    [Fact]
    public void WhenOneOfSeveralDevicesDrops_TheSessionKeepsRunning()
    {
        var dropping = AConnectedDevice("Nq1-DROPS");
        var surviving = AConnectedDevice("Nq1-STAYS");

        _viewModel.IsLogging = true;
        Assert.True(_viewModel.IsLogging);
        surviving.Commands.Clear();

        DropDevice(dropping);

        Assert.True(_viewModel.IsLogging);
        Assert.True(LoggingManager.Instance.Active);
        // Nothing was asked of the device still recording — not a stop, and not a restart either.
        Assert.Empty(surviving.Commands);
    }

    /// <summary>
    /// And the fleet draining to empty one device at a time still closes the run. The condition is
    /// "nothing is left connected", not "this was the only device there ever was".
    /// </summary>
    [Fact]
    public void WhenEveryDeviceHasDroppedInTurn_TheSessionIsClosed()
    {
        var first = AConnectedDevice("Nq1-A");
        var second = AConnectedDevice("Nq1-B");

        _viewModel.IsLogging = true;

        DropDevice(first);
        Assert.True(_viewModel.IsLogging);

        DropDevice(second);

        Assert.False(_viewModel.IsLogging);
        Assert.False(LoggingManager.Instance.Active);
    }

    /// <summary>
    /// The guard: a drop that arrives with no session running must not pretend one ended. Writing
    /// <see cref="DaqifiViewModel.IsLogging"/> unconditionally would run the whole stop path —
    /// disk-space monitor off, a fleet-wide stop fan-out, <c>LoggingManager.Active</c> written —
    /// for a session that was never started.
    /// </summary>
    [Fact]
    public void WhenTheLastDeviceDropsWithNoSessionRunning_NothingIsStopped()
    {
        var device = AConnectedDevice("Nq1-IDLE");
        Assert.False(_viewModel.IsLogging);
        device.Commands.Clear();

        DropDevice(device);

        Assert.False(_viewModel.IsLogging);
        Assert.False(LoggingManager.Instance.Active);
        Assert.Empty(device.Commands);
    }

    /// <summary>An <see cref="IFirmwareDownloadService"/> that reaches no network.</summary>
    private sealed class OfflineDownloadService : IFirmwareDownloadService
    {
        public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(
            string destinationDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult<(string, string)?>(null);

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

        public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FirmwareReleaseInfo?>(null);

        public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(
            string deviceVersionString,
            bool includePreRelease = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InvalidateCache() { }
    }
}
