using System.Reflection;
using Daqifi.Desktop;
using Daqifi.Desktop.Device.SerialDevice;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.UpdateVersion;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// #348: an app update restarts the app, and a restart is destructive during a logging session, a
/// firmware update, or an SD card transfer. These tests set the flags the app sets in each state,
/// and read the answer of the view model's gate (<see cref="DaqifiViewModel.CurrentUpdateBlocker"/>).
/// They then check the result through the real gate: no Install button while the state holds,
/// and the button back when it clears. <see cref="Daqifi.Avalonia.Tests.UpdateVersion.AppUpdateNotifierTests"/>
/// covers what the update does with each answer.
/// </summary>
/// <remarks>
/// In <see cref="ConnectionManagerSingletonCollection"/> because the firmware state is
/// <c>ConnectionManager.Instance.DeviceBeingUpdated</c> and the logging state writes
/// <c>LoggingManager.Instance.Active</c>, both process-wide. <c>App.InitializeMobile()</c> gives
/// <c>LoggingManager.Instance</c> its database, in the throwaway data directory that the assembly's
/// module initializer set. The one firmware signal not covered here is the bootloader watcher's,
/// which the gate reads from <c>App.ServiceProvider</c>, which a test cannot replace.
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class UpdateBlockerConditionTests : IDisposable
{
    private readonly DaqifiViewModel _viewModel;

    public UpdateBlockerConditionTests()
    {
        Daqifi.Desktop.App.InitializeMobile();
        _viewModel = new DaqifiViewModel(new NullDialogService());
    }

    public void Dispose()
    {
        // Both are process-wide singletons, so anything left here goes to the next test.
        try { LoggingManager.Instance.Active = false; } catch { /* best-effort cleanup */ }
        ConnectionManager.Instance.DeviceBeingUpdated = null;
    }

    [Fact]
    public void LoggingSession_HoldsTheUpdate_UntilItStops()
    {
        Assert.Equal(UpdateBlocker.None, _viewModel.CurrentUpdateBlocker());

        _viewModel.IsLogging = true;
        Assert.True(_viewModel.IsLogging);
        Assert.Equal(UpdateBlocker.Logging, _viewModel.CurrentUpdateBlocker());

        _viewModel.IsLogging = false;
        Assert.Equal(UpdateBlocker.None, _viewModel.CurrentUpdateBlocker());
    }

    /// <summary>
    /// After logging stops, the session is saved for a moment in the background (#503), and the
    /// update waits for that too. Driven through the saver's counter, because the save of a session
    /// with no samples ends at once.
    /// </summary>
    [Fact]
    public void SessionStillBeingSaved_HoldsTheUpdate_UntilItIsSaved()
    {
        var counter = typeof(LoggingManager).GetField("_sessionsBeingSaved", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var manager = LoggingManager.Instance;
        var before = (int)counter.GetValue(manager)!;
        try
        {
            counter.SetValue(manager, before + 1);
            Assert.True(manager.IsSavingSession);
            Assert.Equal(UpdateBlocker.Logging, _viewModel.CurrentUpdateBlocker());

            counter.SetValue(manager, before);
            Assert.Equal(UpdateBlocker.None, _viewModel.CurrentUpdateBlocker());
        }
        finally
        {
            counter.SetValue(manager, before);
        }
    }

    /// <summary>
    /// A firmware update that the coordinator runs. The connection manager marks the device for the
    /// whole update, including the reboot and reconnect around the write.
    /// </summary>
    [Fact]
    public void FirmwareUpdate_HoldsTheUpdate_UntilItEnds()
    {
        ConnectionManager.Instance.DeviceBeingUpdated = new SerialStreamingDevice("COM-TEST-348");
        Assert.Equal(UpdateBlocker.FirmwareUpdate, _viewModel.CurrentUpdateBlocker());

        ConnectionManager.Instance.DeviceBeingUpdated = null;
        Assert.Equal(UpdateBlocker.None, _viewModel.CurrentUpdateBlocker());
    }

    /// <summary>A firmware upload started from the Devices pane, which sets the view model's own flag.</summary>
    [Fact]
    public void FirmwareUploadFromTheDevicesPane_HoldsTheUpdate_UntilItEnds()
    {
        _viewModel.IsFirmwareUploading = true;
        Assert.Equal(UpdateBlocker.FirmwareUpdate, _viewModel.CurrentUpdateBlocker());

        _viewModel.IsFirmwareUploading = false;
        Assert.Equal(UpdateBlocker.None, _viewModel.CurrentUpdateBlocker());
    }

    /// <summary>
    /// The Logged Data pane imports a file or deletes sessions. This is not one of the ticket's three
    /// states, but it is the same gate, and no other test sets this flag.
    /// </summary>
    [Fact]
    public void LoggedDataBusy_HoldsTheUpdate_UntilItEnds()
    {
        _viewModel.IsLoggedDataBusy = true;
        Assert.Equal(UpdateBlocker.LoggedDataBusy, _viewModel.CurrentUpdateBlocker());

        _viewModel.IsLoggedDataBusy = false;
        Assert.Equal(UpdateBlocker.None, _viewModel.CurrentUpdateBlocker());
    }

    /// <summary>
    /// The whole rule through the real gate, for each of the ticket's three states. While the state
    /// holds, the update's notification has no Install button and says why. When the state clears,
    /// the next look of the watch puts the button back. Nothing is downloaded or installed.
    /// </summary>
    [Theory]
    [InlineData("logging", "when logging stops")]
    [InlineData("firmware", "when the firmware update finishes")]
    [InlineData("sd card", "when the SD card transfer finishes")]
    public async Task UpdateOffer_HasNoInstallButton_WhileTheStateHolds(string state, string waitingFor)
    {
        var (start, stop) = StateSwitch(state);
        var service = new OneUpdateService();
        var shown = new List<Notifications?>();
        using var stopWatching = new CancellationTokenSource();
        var looks = 0;
        Action onLook = () => { };
        var notifier = new AppUpdateNotifier(
            service,
            _viewModel.CurrentUpdateBlocker,
            (_, _) => Task.FromResult(true),
            shown.Add,
            new RecordingAppLogger(),
            (_, token) =>
            {
                onLook();
                return token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;
            });

        start();
        try
        {
            var offer = await notifier.CheckAsync();

            Assert.Equal($"DAQiFi 3.4.0 is available. Install it {waitingFor}.", offer!.Message);
            Assert.Null(offer.ActionText);
        }
        finally
        {
            stop();
        }

        onLook = () => { if (++looks > 1) { stopWatching.Cancel(); } };
        await notifier.WatchAsync(stopWatching.Token);

        Assert.Equal("DAQiFi 3.4.0 is available.", shown.Last()!.Message);
        Assert.Equal("Install and restart", shown.Last()!.ActionText);
        Assert.Equal(0, service.Downloads);
        Assert.Equal(0, service.Applies);
    }

    /// <summary>How each of the ticket's three states starts and ends, through the flags the app sets.</summary>
    private (Action Start, Action Stop) StateSwitch(string state)
    {
        switch (state)
        {
            case "logging":
                return (() => _viewModel.IsLogging = true, () => _viewModel.IsLogging = false);
            case "firmware":
                return (
                    () => ConnectionManager.Instance.DeviceBeingUpdated = new SerialStreamingDevice("COM-TEST-348"),
                    () => ConnectionManager.Instance.DeviceBeingUpdated = null);
            case "sd card":
                // The app creates the pane only in a started host; the test puts one in place, as
                // UpdateBlockerImportTests does.
                var deviceLogs = new DeviceLogsViewModel(new RecordingAppLogger(), importer: null, marshalToUiThread: a => a());
                typeof(DaqifiViewModel).GetProperty(nameof(DaqifiViewModel.DeviceLogsViewModel))!
                    .SetValue(_viewModel, deviceLogs);
                return (deviceLogs.BeginBusy, deviceLogs.EndBusy);
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }

    /// <summary>An update service that offers 3.4.0 and counts what is asked of it. Reaches no network.</summary>
    private sealed class OneUpdateService : IAppUpdateService
    {
        public AppUpdateSupport Support => AppUpdateSupport.Supported;

        public bool ApplyNeedsElevation => false;

        public int Downloads { get; private set; }

        public int Applies { get; private set; }

        public Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AppUpdateOffer?>(new AppUpdateOffer("3.4.0", null));

        public Task DownloadAsync(AppUpdateOffer offer, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            Downloads++;
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(AppUpdateOffer offer) => Applies++;

        public bool IsInstallInUseElsewhere() => false;
    }
}
