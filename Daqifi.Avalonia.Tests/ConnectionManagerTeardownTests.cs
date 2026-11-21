using Daqifi.Avalonia.Tests.Device;
using Daqifi.Core.Device;
using Daqifi.Desktop;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Pins what happens to a device the app is holding when its connection goes away underneath it.
///
/// The accumulation these cover is per-drop and permanent for the process: a device left in
/// <see cref="ConnectionManager.ConnectedDevices"/> keeps its wrapper, its Core device and that
/// device's transport — for WiFi, an open socket — reachable from a static singleton, because the
/// only code that disposes them is <c>Disconnect</c>, which nothing was calling. Its channels stay
/// in <c>LoggingManager</c> marked active, so they keep counting toward "can start logging" and
/// keep being listed as live inputs on a device that is gone.
///
/// The <c>WasNotTornDown</c> assertions are the regression half: each is the state the app was
/// actually left in before this handler existed, and each fails if the teardown is removed or
/// short-circuited.
/// </summary>
public class ConnectionManagerTeardownTests
{
    /// <summary>
    /// Stands in for <c>LoggingManager</c>, which cannot be constructed without a running Avalonia
    /// application (its constructor resolves an EF Core context factory out of
    /// <c>App.ServiceProvider</c>). Holds what the logger holds, and records what teardown releases.
    /// </summary>
    private sealed class FakeSubscriptions
    {
        private readonly List<IChannel> _subscribed = [];

        public List<IChannel> Released { get; } = [];

        public IReadOnlyList<IChannel> Subscribed => _subscribed.ToList();

        public void Subscribe(IChannel channel) => _subscribed.Add(channel);

        public void Unsubscribe(IChannel channel)
        {
            Released.Add(channel);
            _subscribed.RemoveAll(c => ReferenceEquals(c, channel));
        }
    }

    /// <summary>
    /// A manager with its own device list, its logger replaced by <paramref name="subscriptions"/>,
    /// and its UI marshal run inline (outside a running application, Avalonia's dispatcher queues onto a thread nothing
    /// pumps). Everything else — the teardown, the firmware-update carve-out, the subscription
    /// bookkeeping, the notification — is the production code path.
    /// </summary>
    private static ConnectionManager NewManager(FakeSubscriptions subscriptions) =>
        NewManager(subscriptions, action => action());

    /// <summary>The same manager, with its UI marshal supplied by the test.</summary>
    private static ConnectionManager NewManager(FakeSubscriptions subscriptions, Action<Action> postToUiThread) =>
        new(() => subscriptions.Subscribed,
            subscriptions.Unsubscribe,
            postToUiThread);

    /// <summary>
    /// A connected device whose channels are BOTH on the device and in the logger — the state a
    /// device is in once the user has selected channels to record.
    /// </summary>
    private static DroppableTestDevice ConnectedWifiDevice(
        ConnectionManager manager,
        FakeSubscriptions subscriptions,
        string serial = "SN-WIFI-1",
        int channelCount = 2)
    {
        var device = new DroppableTestDevice(serial);
        for (var i = 0; i < channelCount; i++)
        {
            var channel = new FakeChannel($"AI{i}", serial, index: i);
            device.DataChannels.Add(channel);
            subscriptions.Subscribe(channel);
        }

        manager.ConnectedDevices.Add(device);
        manager.SubscribeDeviceEvents(device);
        return device;
    }

    /// <summary>
    /// The one case that goes through <see cref="ConnectionManager.Connect"/> itself, so the
    /// wiring the other cases set up by hand is pinned to what the production connect path
    /// actually does. Everything else uses the hand-wired helper, so that each case wires only
    /// what it tests.
    /// </summary>
    /// <remarks>
    /// The drop comes at once after the connect returns. Before #617 a drop at this time landed
    /// inside a fixed 1 s wait at the end of the connect. Now it is an ordinary drop of a
    /// connected device, and the loss handler must still remove it, release its channels and
    /// name it.
    /// </remarks>
    [Fact]
    public async Task Connecting_a_device_wires_it_for_teardown()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var device = new DroppableTestDevice("SN-CONNECTED") { PretendConnectSucceeds = true };
        var channel = new FakeChannel("AI0", "SN-CONNECTED");
        device.DataChannels.Add(channel);
        subs.Subscribe(channel);

        await manager.Connect(device);
        Assert.Contains(device, manager.ConnectedDevices);

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.DoesNotContain(device, manager.ConnectedDevices);
        Assert.Single(subs.Released);
        Assert.Contains("SN-CONNECTED", manager.LastDisconnectReason);
    }

    /// <summary>
    /// The other half of the same window. A transport that dies between <c>device.Connect()</c>
    /// returning and the loss handler being wired is unobservable — no subscriber exists yet, and
    /// even with one the handler would find the device not yet in <c>ConnectedDevices</c> and
    /// return. Accepting it anyway would put the stale entry into the connected list that this
    /// whole class now exists to remove, and report it to the user as a successful connect.
    /// </summary>
    [Fact]
    public async Task A_device_whose_transport_died_before_it_was_accepted_is_not_added()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var device = new DroppableTestDevice("SN-STILLBORN") { PretendTransportDiesDuringConnect = true };

        await manager.Connect(device);

        Assert.DoesNotContain(device, manager.ConnectedDevices);
        Assert.Equal(DAQiFiConnectionStatus.Error, manager.ConnectionStatus);
    }

    /// <summary>
    /// Core can report Lost after the loss handler is attached and before Connect returns. In the
    /// app the handler only POSTS its teardown to the UI thread, which is the thread Connect's
    /// continuation runs on, so that teardown waits behind the connect. Connect must read the
    /// device's own state, tear the device down itself, and report an error, not Connected. The
    /// queued teardown must then do nothing, so the device is torn down once.
    /// </summary>
    /// <remarks>
    /// The UI marshal here only queues, like a UI thread that is busy with this connect. Without
    /// the device-state check, Connect found the device still in ConnectedDevices and returned
    /// Connected; the removal came only after the dialog had closed.
    /// </remarks>
    [Fact]
    public async Task A_loss_reported_while_the_connect_finishes_fails_the_connect_and_tears_down_once()
    {
        var subs = new FakeSubscriptions();
        var queued = new List<Action>();
        var manager = NewManager(subs, queued.Add);
        var device = new DroppableTestDevice("SN-LOST-WIRED") { PretendLostOnceWired = true };
        var channel = new FakeChannel("AI0", "SN-LOST-WIRED");
        device.DataChannels.Add(channel);
        subs.Subscribe(channel);

        var result = await manager.Connect(device);

        Assert.False(result.IsConnected);
        Assert.Equal(DAQiFiConnectionStatus.Error, manager.ConnectionStatus);
        Assert.DoesNotContain(device, manager.ConnectedDevices);
        Assert.Single(subs.Released);
        Assert.Equal(1, device.DisconnectCalls);
        Assert.Contains("SN-LOST-WIRED", manager.LastDisconnectReason);

        // The loss handler's own teardown, posted while the connect ran, now reaches the UI thread.
        Assert.NotEmpty(queued);
        foreach (var action in queued.ToList())
        {
            action();
        }

        Assert.Single(subs.Released);
        Assert.Equal(1, device.DisconnectCalls);
    }

    /// <summary>
    /// Connect must return as soon as the device is ready, with no fixed wait after it (#617).
    /// Upstream waited 1 s after <c>device.Connect()</c> so that the first status message could
    /// fill in the firmware version and serial number. <c>device.Connect()</c> now waits for that
    /// condition itself, so the fixed second only delayed every connect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test reads no clock (#688). An earlier version asserted that the connect took less
    /// than 500 ms. That limit also measured the wait for a thread-pool thread before
    /// <c>device.Connect()</c>, so it failed on a busy CI runner when no fixed wait was present.
    /// </para>
    /// <para>
    /// A fixed wait is a timer. Its continuation is not ready until the timer fires, and then
    /// the timer's thread posts it. So the test starts the connect on a UI thread that it turns
    /// by hand, lets <c>device.Connect()</c> return, and runs only the work that is ready at that
    /// moment. If the connect is not complete then, it waits for something that is not ready: a
    /// fixed wait came back. The result does not change with the load on the machine. The awaits
    /// in this test have no time limit, so a slow runner only makes the test take longer.
    /// </para>
    /// <para>
    /// This works only if the code after <c>device.Connect()</c> comes back to the caller's thread,
    /// as it does on Avalonia's dispatcher. If that code runs on the thread pool, a wait there is
    /// not seen here: the connect then posts to this thread only when it is complete. So the test
    /// also asserts that the device is listed on the caller's thread.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Connect_returns_without_a_fixed_settle_after_the_device_is_ready()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var deviceReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new DroppableTestDevice("SN-PROMPT") { PretendConnectSucceeds = true, ConnectGate = deviceReady };
        var ui = new HandPumpedUiThread();
        SynchronizationContext? listedOn = null;
        manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionManager.ConnectedDevices))
            {
                listedOn = SynchronizationContext.Current;
            }
        };

        // device.Connect() is held, so the connect must stop at its await before the device is
        // ready. Without the gate, device.Connect() could return before that await, and the rest
        // of the connect would then run here, not on the UI thread.
        var connecting = ui.Start(() => manager.Connect(device));
        Assert.False(connecting.IsCompleted);

        deviceReady.SetResult();
        await Task.WhenAny(ui.FirstPost, connecting);
        ui.RunReadyWork();

        Assert.True(connecting.IsCompleted,
            "Connect still waits after device.Connect() returned and no work is ready: a fixed wait came back.");
        Assert.True(ReferenceEquals(ui, listedOn),
            "Connect did not list the device on the caller's thread, so this test cannot see a wait after device.Connect().");
        var result = await connecting;
        Assert.True(result.IsConnected);
        Assert.Contains(device, manager.ConnectedDevices);
    }

    /// <summary>
    /// A UI thread that the test turns by hand. A continuation posted to it stays in a queue until
    /// the test runs it, and the queue records which thread posted it.
    /// </summary>
    private sealed class HandPumpedUiThread : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State, int Poster)> _posts = new();
        private readonly TaskCompletionSource _firstPost = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the first continuation is posted.</summary>
        public Task FirstPost => _firstPost.Task;

        public override void Post(SendOrPostCallback d, object? state)
        {
            _posts.Enqueue((d, state, Environment.CurrentManagedThreadId));
            _firstPost.TrySetResult();
        }

        /// <summary>Calls <paramref name="start"/> with this context current, so its awaits come back here.</summary>
        public T Start<T>(Func<T> start)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                return start();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        /// <summary>
        /// Runs the continuations that are ready now: the ones in the queue when this is called,
        /// and the ones that they post from this thread while they run. A continuation that
        /// another thread posts during this call (a timer that fires) stays in the queue.
        /// </summary>
        public void RunReadyWork()
        {
            var pumpThread = Environment.CurrentManagedThreadId;
            var queuedBefore = _posts.Count;
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_posts.TryPeek(out var next) && (queuedBefore > 0 || next.Poster == pumpThread))
                {
                    _posts.TryDequeue(out _);
                    queuedBefore--;
                    next.Callback(next.State);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    [Fact]
    public void A_dropped_wifi_device_is_removed_from_the_connected_list()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var device = ConnectedWifiDevice(manager, subs);

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.DoesNotContain(device, manager.ConnectedDevices);
    }

    [Fact]
    public void A_dropped_wifi_device_releases_every_one_of_its_channels()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var device = ConnectedWifiDevice(manager, subs, channelCount: 3);
        var expected = device.DataChannels.ToList();

        device.ReportCoreStatus(ConnectionStatus.Lost);

        // By instance: the channels released must be this device's own objects, not merely equal
        // ones — AbstractChannel.Equals compares the input, and an earlier connection's instance of
        // the same input is equal to this one.
        Assert.Equal(3, subs.Released.Count);
        Assert.All(expected, channel => Assert.Contains(subs.Released, r => ReferenceEquals(r, channel)));
    }

    /// <summary>
    /// Teardown must not take the device's word for which channels are its own. Core rebuilds
    /// <c>DataChannels</c> with a <c>Clear</c> followed by an <c>AddRange</c>, so a read landing in
    /// that interval comes back short — or, as here, empty — without throwing anything. Every
    /// channel missed that way stays in the logger marked active for the process lifetime, still
    /// counting toward <c>CanToggleLogging</c>: the leak this class exists to close, reintroduced
    /// through a race. The logger's own list has no such window, so that is what teardown reads.
    /// </summary>
    [Fact]
    public void Channels_are_released_even_if_the_device_list_is_being_rebuilt()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var device = ConnectedWifiDevice(manager, subs, serial: "SN-REBUILDING", channelCount: 3);

        // Exactly what a reader sees between Core's Clear() and its AddRange().
        device.DataChannels.Clear();

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Equal(3, subs.Released.Count);
        Assert.Empty(subs.Subscribed);
        Assert.DoesNotContain(device, manager.ConnectedDevices);
    }

    /// <summary>
    /// The serial number is what identifies a device's subscriptions, so a device that has not
    /// reported one cannot be matched that way — a blank serial would sweep up every other
    /// unidentified device's channels. That case alone falls back to the device's own list.
    /// </summary>
    [Fact]
    public void A_device_with_no_serial_number_falls_back_to_its_own_channel_list()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);

        var anonymous = new DroppableTestDevice(string.Empty);
        var ownChannel = new FakeChannel("AI0", string.Empty);
        anonymous.DataChannels.Add(ownChannel);
        subs.Subscribe(ownChannel);
        manager.ConnectedDevices.Add(anonymous);
        manager.SubscribeDeviceEvents(anonymous);

        var identified = ConnectedWifiDevice(manager, subs, serial: "SN-IDENTIFIED", channelCount: 2);

        anonymous.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Equal([ownChannel], subs.Released);
        Assert.Equal(2, identified.DataChannels.Count);
        Assert.Contains(identified, manager.ConnectedDevices);
    }

    /// <summary>
    /// The teardown must leave the logger holding nothing for the dropped device, and must still
    /// disconnect it. Both halves in one case, because an ordering or short-circuit change that
    /// breaks either one looks harmless in isolation: channels left subscribed keep counting toward
    /// <c>CanToggleLogging</c> for the process lifetime, and a device left connected is the whole
    /// bug.
    /// </summary>
    [Fact]
    public void Teardown_leaves_neither_a_subscribed_channel_nor_a_connected_device()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var device = ConnectedWifiDevice(manager, subs, channelCount: 2);

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Equal(2, subs.Released.Count);
        Assert.Empty(subs.Subscribed);
        Assert.Empty(device.DataChannels);
        Assert.DoesNotContain(device, manager.ConnectedDevices);
    }

    [Fact]
    public void The_user_is_told_which_device_went_away_and_why()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var device = ConnectedWifiDevice(manager, subs, serial: "Nq1-0042");

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.True(manager.NotifyConnection);
        Assert.Contains("Nq1-0042", manager.LastDisconnectReason);
        Assert.Contains("connection lost", manager.LastDisconnectReason);
    }

    /// <summary>
    /// A device being flashed drops its transport when it reboots into the bootloader and back —
    /// an expected part of the update that Core reconnects itself. Tearing it down here disposes
    /// the Core device out from under Core's reconnect and times the update out even though the
    /// flash succeeded (issue #738), so this carve-out is load-bearing, not defensive.
    /// </summary>
    [Fact]
    public void A_device_being_firmware_updated_is_left_alone()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var device = ConnectedWifiDevice(manager, subs);
        manager.DeviceBeingUpdated = device;

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Contains(device, manager.ConnectedDevices);
        Assert.Empty(subs.Released);
        Assert.False(manager.NotifyConnection);
        Assert.Equal(2, device.DataChannels.Count);
    }

    /// <summary>
    /// A drop can be reported more than once — the app ran its own USB watcher beside Core's until
    /// #441, and a transport can still report a loss after the device has gone. The second report
    /// must find nothing to do rather than disconnect a device already gone and raise a second
    /// notification.
    /// </summary>
    [Fact]
    public void A_second_report_for_the_same_drop_does_nothing()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var device = ConnectedWifiDevice(manager, subs, channelCount: 2);

        device.ReportCoreStatus(ConnectionStatus.Lost);
        manager.NotifyConnection = false;
        manager.LastDisconnectReason = string.Empty;

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Equal(2, subs.Released.Count);
        Assert.False(manager.NotifyConnection);
        Assert.Equal(string.Empty, manager.LastDisconnectReason);
    }

    /// <summary>
    /// The symmetry that keeps the fix from becoming its own leak: an explicitly disconnected
    /// device must not still be wired to this manager. A wrapper that outlives its session — the
    /// connection dialog keeps discovered devices around — would otherwise hold the singleton
    /// manager reachable through the handler, and a late drop report would tear down a device the
    /// user already disconnected.
    /// </summary>
    [Fact]
    public void An_explicitly_disconnected_device_is_unwired_from_the_manager()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var device = ConnectedWifiDevice(manager, subs);
        var other = ConnectedWifiDevice(manager, subs, serial: "SN-WIFI-2");

        manager.Disconnect(device);
        subs.Released.Clear();

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.False(manager.NotifyConnection);
        Assert.Empty(subs.Released);
        // The manager is otherwise intact: the drop reached nobody, it did not empty the list.
        Assert.Contains(other, manager.ConnectedDevices);
    }

    /// <summary>
    /// Connecting the same wrapper twice without an explicit disconnect in between must not double
    /// the wiring: one drop, one teardown, one notification.
    /// </summary>
    [Fact]
    public void Subscribing_twice_still_tears_down_once()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var device = ConnectedWifiDevice(manager, subs, channelCount: 2);
        manager.SubscribeDeviceEvents(device);

        device.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.Equal(2, subs.Released.Count);
        Assert.DoesNotContain(device, manager.ConnectedDevices);
    }

    /// <summary>
    /// One device dropping must not disturb the others the user still has connected.
    /// </summary>
    [Fact]
    public void Only_the_device_that_dropped_is_torn_down()
    {
        var subs = new FakeSubscriptions();
        var manager = NewManager(subs);
        var dropped = ConnectedWifiDevice(manager, subs, serial: "SN-A", channelCount: 2);
        var kept = ConnectedWifiDevice(manager, subs, serial: "SN-B", channelCount: 2);

        dropped.ReportCoreStatus(ConnectionStatus.Lost);

        Assert.DoesNotContain(dropped, manager.ConnectedDevices);
        Assert.Contains(kept, manager.ConnectedDevices);
        Assert.Equal(2, kept.DataChannels.Count);
        Assert.All(subs.Released, channel => Assert.Equal("SN-A", channel.DeviceSerialNo));
    }
}
