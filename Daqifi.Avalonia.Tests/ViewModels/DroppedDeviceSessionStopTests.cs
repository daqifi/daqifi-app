using System.ComponentModel;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Core.Firmware;
using Daqifi.Desktop;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Xunit;
using DeviceMode = Daqifi.Desktop.Device.DeviceMode;
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

    // ---------------------------------------------------------------------------------------------
    // Issue #537: the same stuck state, reached by the user's own DISCONNECT or REBOOT.
    //
    // DisconnectDevice and RebootDevice release the device's channels and then call
    // ConnectionManager.Disconnect / Reboot, which raise ConnectedDevices only — never
    // NotifyConnection. So the #525 stop above, which lived only in the NotifyConnection case, never
    // ran: LOGGING ON stayed lit over a disabled toggle, LoggingManager.Active stayed true, and the
    // disk-space monitor kept running with nothing connected.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// What <c>ConnectionManager</c> raises after <c>Disconnect</c> and <c>Reboot</c>. Called
    /// directly for the same reason as <see cref="DropDevice"/>: the view model only subscribes to
    /// the manager when <c>App.IsStarted</c>.
    /// </summary>
    private void RaiseConnectedDevicesChanged()
        => _viewModel.UpdateUi(
            ConnectionManager.Instance,
            new PropertyChangedEventArgs(nameof(ConnectionManager.ConnectedDevices)));

    /// <summary>
    /// The report itself, through the real command and the real <c>ConnectionManager.Disconnect</c>.
    /// The session closes, and the toggle is left OFF rather than ON-and-disabled. It is also
    /// disabled now, correctly: nothing is connected and no session is running, so there is nothing
    /// to start and nothing to stop.
    /// </summary>
    [Fact]
    public void WhenTheUserDisconnectsTheLastDevice_TheSessionIsClosed()
    {
        var device = AConnectedDevice("Nq1-DISCONNECT");

        _viewModel.IsLogging = true;
        Assert.True(LoggingManager.Instance.Active);

        _viewModel.DisconnectDeviceCommand.Execute(device);
        _registered.Remove(device);
        RaiseConnectedDevicesChanged();

        Assert.Empty(ConnectionManager.Instance.ConnectedDevices);
        Assert.False(_viewModel.IsLogging);
        Assert.False(LoggingManager.Instance.Active);
        Assert.False(_viewModel.CanToggleLogging);
    }

    /// <summary>The same through REBOOT, which reaches the manager by its own method.</summary>
    [Fact]
    public void WhenTheUserRebootsTheLastDevice_TheSessionIsClosed()
    {
        var device = AConnectedDevice("Nq1-REBOOT");

        _viewModel.IsLogging = true;
        Assert.True(LoggingManager.Instance.Active);

        _viewModel.RebootDeviceCommand.Execute(device);
        _registered.Remove(device);
        RaiseConnectedDevicesChanged();

        Assert.Empty(ConnectionManager.Instance.ConnectedDevices);
        Assert.False(_viewModel.IsLogging);
        Assert.False(LoggingManager.Instance.Active);
    }

    /// <summary>
    /// After the close, the user can start again: a device connects and the next start opens a new
    /// session and commands that device. Before the fix the old session was still open, so the
    /// reconnected device joined a run the user could not see the start of.
    /// </summary>
    [Fact]
    public void AfterTheUserDisconnectsTheLastDevice_ANewSessionCanStartOnReconnect()
    {
        var first = AConnectedDevice("Nq1-AGAIN");
        _viewModel.IsLogging = true;

        _viewModel.DisconnectDeviceCommand.Execute(first);
        _registered.Remove(first);
        RaiseConnectedDevicesChanged();
        Assert.False(_viewModel.IsLogging);

        var reconnected = new RecordingStreamingDevice("Nq1-AGAIN");
        ConnectionManager.Instance.RegisterConnectedDevice(reconnected);
        _registered.Add(reconnected);
        RaiseConnectedDevicesChanged();
        Assert.Contains(reconnected, _viewModel.ConnectedDevices);

        _viewModel.IsLogging = true;

        Assert.True(_viewModel.IsLogging);
        Assert.True(LoggingManager.Instance.Active);
        Assert.NotEmpty(reconnected.Commands);
    }

    /// <summary>
    /// The half that keeps the condition honest for the manual path too: the user disconnects one
    /// of two devices, and the run belongs to the device that is still recording.
    /// </summary>
    [Fact]
    public void WhenTheUserDisconnectsOneOfSeveralDevices_TheSessionKeepsRunning()
    {
        var leaving = AConnectedDevice("Nq1-LEAVES");
        var staying = AConnectedDevice("Nq1-REMAINS");

        _viewModel.IsLogging = true;
        staying.Commands.Clear();

        _viewModel.DisconnectDeviceCommand.Execute(leaving);
        _registered.Remove(leaving);
        RaiseConnectedDevicesChanged();

        Assert.True(_viewModel.IsLogging);
        Assert.True(LoggingManager.Instance.Active);
        Assert.Empty(staying.Commands);
    }

    /// <summary>
    /// The toggle can never be ON and disabled. A device that kept logging to its SD card across a
    /// desktop reconnect makes <see cref="DaqifiViewModel.IsLogging"/> read true, but no channel is
    /// subscribed, so <c>CanToggleLogging = ActiveChannels.Count > 0</c> disabled the very switch
    /// that would stop it. A running session must always be stoppable from the toggle.
    /// </summary>
    [Fact]
    public void WhileASessionIsRunningWithNoActiveChannel_TheToggleCanStillBeTurnedOff()
    {
        var device = new RecordingStreamingDevice("Nq1-SD");
        device.StartSdCardLogging();
        device.Commands.Clear();
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);

        RaiseConnectedDevicesChanged();

        Assert.True(_viewModel.IsLogging);
        Assert.Empty(_viewModel.ActiveChannels);
        Assert.True(_viewModel.CanToggleLogging);
    }

    // ---------------------------------------------------------------------------------------------
    // Review of #554: the other half of that rule. When the SD-only run stops, the toggle must go
    // back to disabled — otherwise it reads OFF, enabled, with no active channel, and a click starts
    // a session that has nothing to record. The device's own announcement and the setter's refused-
    // stop bookkeeping both change IsLogging without passing through UpdateUi, which is where the
    // rule was evaluated.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A device that reconnects while it logs to its SD card, with no channel subscribed on this
    /// desktop. <see cref="RecordingStreamingDevice.AnnouncesSdCardLogging"/> makes it announce its
    /// SD state the way the real device does, which is the notification under test.
    /// </summary>
    private RecordingStreamingDevice AnSdOnlyRunAfterReconnect(string serial)
    {
        var device = new RecordingStreamingDevice(serial) { AnnouncesSdCardLogging = true };
        device.SwitchMode(DeviceMode.LogToDevice);
        device.StartSdCardLogging();
        device.Commands.Clear();
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
        RaiseConnectedDevicesChanged();

        Assert.True(_viewModel.IsLogging);
        Assert.Empty(_viewModel.ActiveChannels);
        Assert.True(_viewModel.CanToggleLogging);
        return device;
    }

    /// <summary>The review's case: the user turns that run OFF from the toggle.</summary>
    [Fact]
    public void WhenTheUserStopsAnSdOnlyRun_TheToggleIsDisabledAgain()
    {
        var device = AnSdOnlyRunAfterReconnect("Nq1-SD-OFF");

        _viewModel.IsLogging = false;

        Assert.Contains("StopSdCardLogging", device.Commands);
        Assert.False(_viewModel.IsLogging);
        Assert.False(_viewModel.CanToggleLogging);
    }

    /// <summary>
    /// The run ends without the toggle — another client sent the stop, or the device stopped by
    /// itself. The device's announcement is then the only signal the app gets.
    /// </summary>
    [Fact]
    public void WhenAnSdOnlyRunStopsOnTheDevice_TheToggleIsDisabledAgain()
    {
        var device = AnSdOnlyRunAfterReconnect("Nq1-SD-SELF");

        device.StopSdCardLogging();

        Assert.False(_viewModel.IsLogging);
        Assert.False(_viewModel.CanToggleLogging);
    }

    /// <summary>
    /// The device refuses the stop. Its flag still reads true, the setter drops that flag as stale
    /// (<c>ForgetSdState</c>), and no device announcement follows — so the setter itself is the
    /// only place that sees IsLogging change.
    /// </summary>
    [Fact]
    public void WhenAnSdOnlyRunRefusesTheStop_TheToggleIsDisabledAgain()
    {
        var device = AnSdOnlyRunAfterReconnect("Nq1-SD-REFUSES");
        device.Refusals["StopSdCardLogging"] = new InvalidOperationException("no answer");

        _viewModel.IsLogging = false;

        Assert.True(device.IsLoggingToSdCard);
        Assert.False(_viewModel.IsLogging);
        Assert.False(_viewModel.CanToggleLogging);
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
