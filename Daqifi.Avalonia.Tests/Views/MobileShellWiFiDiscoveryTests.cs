using System.Net;
using Daqifi.Avalonia.Services;
using Daqifi.Avalonia.Views;
using Daqifi.Core.Device.Discovery;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #516: the mobile discovery sweep runs Core's <see cref="WiFiDeviceFinder"/> pass after pass,
/// and that repetition is what these tests pin.
/// </summary>
/// <remarks>
/// On a Galaxy A16, ONE <c>DiscoverAsync</c> window found the bench board in only 4 of 8 scans, while
/// the old local sweep — which repeated every 4.5 s until stopped — found it almost every time. So a
/// one-call swap would have halved discovery on Android. The first group drives
/// <see cref="MobileShellViewModel.RunDiscoveryPassesAsync"/> directly; the second drives the real
/// <c>ScanCommand</c> with a scripted finder, so no test binds a socket or reaches a real board.
/// <para>
/// In the <c>ConnectionManager</c> singleton collection because a scan enters
/// <see cref="NetworkDiscoveryScope.Current"/>, a process-wide static that
/// <see cref="MobileShellDiscoveryAfterConnectTests"/> (a member of that collection) replaces and
/// counts. Run in parallel, a sweep here would enter that class's counting scope and break its
/// balance check.
/// </para>
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class MobileShellWiFiDiscoveryTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TestWindow = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan TestPause = TimeSpan.FromMilliseconds(10);

    private static readonly IDeviceInfo Board = new DeviceInfo
    {
        Name = "DAQiFi-95AC",
        SerialNumber = "9090539562006014104",
        FirmwareVersion = "3.8.0",
        IPAddress = IPAddress.Parse("192.168.1.30"),
        MacAddress = "F8-F0-05-A4-95-AC",
        // Deliberately NOT Core's default 9760: the connect must use the port the board advertises.
        Port = 9761,
        Type = DeviceType.Nyquist1,
        ConnectionType = ConnectionType.WiFi,
    };

    private readonly object _uiGate = new();
    private readonly CountingDiscoveryScope _scope = new();
    private readonly INetworkDiscoveryScope? _previousScope = NetworkDiscoveryScope.Current;
    private readonly MobileShellViewModel _shell = new();

    public MobileShellWiFiDiscoveryTests()
    {
        NetworkDiscoveryScope.Current = _scope;
        _shell.WifiPassWindow = TestWindow;
        _shell.WifiPassPause = TestPause;
        // No dispatcher runs in the test project, so a posted job would never run. Run it inline,
        // under a gate the assertions also take, because the finder reports on a pool thread.
        _shell.PostToUiThread = action => { lock (_uiGate) { action(); } };
    }

    public void Dispose()
    {
        _shell.Dispose();
        NetworkDiscoveryScope.Current = _previousScope;
    }

    // ---- The loop itself ------------------------------------------------------------------------

    /// <summary>
    /// The requirement from #516. A board that the first pass misses must still be found: the loop
    /// broadcasts again rather than ending on one empty window.
    /// </summary>
    [Fact]
    public async Task A_device_the_first_pass_misses_is_found_by_a_later_pass()
    {
        using var finder = new ScriptedFinder(pass => pass == 0 ? null : Board);
        var found = new List<IDeviceInfo>();
        finder.DeviceDiscovered += (_, e) => { lock (found) { found.Add(e.DeviceInfo); } };
        using var cts = new CancellationTokenSource();

        var loop = MobileShellViewModel.RunDiscoveryPassesAsync(finder, TestWindow, TestPause, cts.Token);

        Assert.True(
            WaitUntil(() => { lock (found) { return found.Count > 0; } }),
            $"No pass found the board after {finder.Passes} pass(es).");
        Assert.True(finder.Passes >= 2, "The board was found on the pass that was scripted to miss it.");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    /// <summary>
    /// A pass that ends because its own window closed is a normal pass, even when the finder reports
    /// that as a cancellation (Core does, if the window closes while the pass waits for its
    /// semaphore). Only the caller's token is a stop.
    /// </summary>
    [Fact]
    public async Task A_window_that_closes_with_a_cancellation_is_not_a_stop()
    {
        using var finder = new ScriptedFinder(_ => null) { ThrowWhenWindowCloses = true };
        using var cts = new CancellationTokenSource();

        var loop = MobileShellViewModel.RunDiscoveryPassesAsync(finder, TestWindow, TestPause, cts.Token);

        Assert.True(
            WaitUntil(() => finder.Passes >= 3 || loop.IsCompleted),
            $"Only {finder.Passes} pass(es) ran.");
        Assert.False(loop.IsCompleted, $"The loop ended on a closed window: {loop.Exception?.InnerException}");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    /// <summary>
    /// A stop is prompt even in the pause between passes — the user taps Stop, or a connect ends the
    /// sweep, and the multicast lock must not wait out a full pause to be released.
    /// </summary>
    [Fact]
    public async Task Cancelling_during_the_pause_ends_the_loop_promptly()
    {
        using var finder = new ScriptedFinder(_ => null);
        using var cts = new CancellationTokenSource();
        var loop = MobileShellViewModel.RunDiscoveryPassesAsync(
            finder, TestWindow, TimeSpan.FromMinutes(10), cts.Token);
        Assert.True(WaitUntil(() => finder.Passes >= 1), "The first pass never started.");
        // Let the first pass's window close, so the loop is waiting in its pause.
        Thread.Sleep(TestWindow * 5);

        cts.Cancel();

        var finished = await Task.WhenAny(loop, Task.Delay(Wait));
        Assert.Same(loop, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        Assert.Equal(1, finder.Passes);
    }

    // ---- Through the real Scan command ----------------------------------------------------------

    /// <summary>
    /// The finder reports a device on every pass that hears it, and the board answers every pass, so
    /// the list must dedupe across passes or it would grow by one row every 4.5 s.
    /// </summary>
    [Fact]
    public void A_device_that_answers_every_pass_is_listed_once()
    {
        var finder = new ScriptedFinder(_ => Board);
        _shell.CreateWifiFinder = () => finder;

        _shell.ScanCommand.Execute(null);

        Assert.True(WaitUntil(() => finder.Passes >= 3), $"Only {finder.Passes} pass(es) ran.");
        lock (_uiGate) { Assert.Single(_shell.Devices); }
    }

    /// <summary>
    /// What the change buys: the row shows the host name the board sent, and the connect uses the
    /// port it advertised, where the old sweep showed <c>DAQiFi @ &lt;ip&gt;</c> and assumed 9760.
    /// </summary>
    [Fact]
    public void A_listed_device_carries_the_identity_and_port_from_its_reply()
    {
        var finder = new ScriptedFinder(_ => Board);
        _shell.CreateWifiFinder = () => finder;

        _shell.ScanCommand.Execute(null);

        Assert.True(WaitUntil(() => { lock (_uiGate) { return _shell.Devices.Count > 0; } }), "Nothing was listed.");
        MobileDeviceItem item;
        lock (_uiGate) { item = _shell.Devices[0]; }
        Assert.Equal("DAQiFi-95AC", item.Name);
        Assert.Equal("192.168.1.30", item.Ip);
        Assert.Equal("192.168.1.30:9761  •  tap to connect", item.Detail);
        Assert.Equal("9090539562006014104", item.Device.SerialNumber);
        Assert.Equal(9761, item.Device.Port);
    }

    /// <summary>
    /// One scope for the whole sweep, not one per pass: on Android it is the multicast lock, and
    /// releasing it between passes would put the power-save filter back while the next pass listens.
    /// Stopping releases it and disposes the finder.
    /// </summary>
    [Fact]
    public void The_discovery_scope_is_held_across_passes_and_released_on_stop()
    {
        var finder = new ScriptedFinder(_ => null);
        _shell.CreateWifiFinder = () => finder;

        _shell.ScanCommand.Execute(null);
        Assert.True(WaitUntil(() => finder.Passes >= 3), $"Only {finder.Passes} pass(es) ran.");
        Assert.Equal(1, _scope.Entered);
        Assert.Equal(0, _scope.Disposed);

        _shell.ScanCommand.Execute(null);   // the same button stops the sweep

        Assert.False(_shell.IsScanning);
        Assert.True(WaitUntil(() => _scope.Disposed == 1), "Stopping left the discovery scope held.");
        Assert.True(WaitUntil(() => finder.IsDisposed), "Stopping left the finder undisposed.");
    }

    /// <summary>
    /// A finder that faults ends the sweep and says so, rather than leaving the button offering to
    /// stop a sweep that is no longer running (#359).
    /// </summary>
    [Fact]
    public void A_finder_that_faults_ends_the_sweep_and_says_why()
    {
        var finder = new ScriptedFinder(_ => null) { Fault = new InvalidOperationException("no socket") };
        _shell.CreateWifiFinder = () => finder;

        _shell.ScanCommand.Execute(null);

        Assert.True(WaitUntil(() => { lock (_uiGate) { return !_shell.IsScanning; } }), "The sweep did not end.");
        lock (_uiGate) { Assert.Equal("Scan failed: no socket", _shell.Status); }
        Assert.True(WaitUntil(() => _scope.Disposed == _scope.Entered), "The faulted sweep left its scope held.");
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) { return true; }
            Thread.Sleep(10);
        }

        return condition();
    }

    /// <summary>
    /// A WiFi finder that binds no socket. Each pass reports the device the script names for that
    /// pass (or none), then listens until its window closes — the shape of Core's real pass.
    /// </summary>
    private sealed class ScriptedFinder(Func<int, IDeviceInfo?> replyForPass) : WiFiDeviceFinder
    {
        private int _passes;
        private int _disposed;

        public int Passes => Volatile.Read(ref _passes);
        public new bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>End each pass by throwing <see cref="OperationCanceledException"/>.</summary>
        public bool ThrowWhenWindowCloses { get; init; }

        /// <summary>Throw this from every pass instead of listening.</summary>
        public Exception? Fault { get; init; }

        public override async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            var pass = Interlocked.Increment(ref _passes) - 1;
            if (Fault != null) { throw Fault; }

            var reply = replyForPass(pass);
            if (reply != null) { OnDeviceDiscovered(reply); }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ThrowWhenWindowCloses)
            {
                // Core's receive loops end quietly when the window closes.
            }

            return reply == null ? [] : [reply];
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Exchange(ref _disposed, 1);
            base.Dispose(disposing);
        }
    }

    /// <summary>Counts discovery scopes entered and released — Android's multicast lock, in tests.</summary>
    private sealed class CountingDiscoveryScope : INetworkDiscoveryScope
    {
        private int _entered;
        private int _disposed;

        public int Entered => Volatile.Read(ref _entered);
        public int Disposed => Volatile.Read(ref _disposed);

        public IDisposable Enter()
        {
            Interlocked.Increment(ref _entered);
            return new Handle(this);
        }

        private sealed class Handle(CountingDiscoveryScope owner) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    Interlocked.Increment(ref owner._disposed);
                }
            }
        }
    }
}
