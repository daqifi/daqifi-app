using Daqifi.Avalonia.Services;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Avalonia.Views;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CoreStreamingDevice = Daqifi.Core.Device.DaqifiStreamingDevice;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #359: when the mobile discovery sweep ends. A successful connect has to end it — there is
/// nothing left to discover once the user has picked a device — and on Android the sweep holds a
/// <c>WifiManager.MulticastLock</c> for exactly as long as it runs, which keeps WiFi power-save
/// broadcast filtering off for the whole connected session on a battery-powered phone.
/// </summary>
/// <remarks>
/// These tests drive the real <c>ScanCommand</c>, so a real sweep starts and really does broadcast
/// on UDP 30303 for a moment before the connect cancels it; what they replace is only the platform
/// discovery scope, with <see cref="CountingDiscoveryScope"/> standing in for Android's multicast
/// lock. That substitution is the point: the scope is the thing the issue measured with
/// <c>dumpsys wifi</c>, and counting entries against disposals is the same balance check at the
/// view-model level.
/// <para>
/// In the <c>ConnectionManager</c> singleton collection because the shell's adopt step registers
/// the device with <c>ConnectionManager.Instance</c>, and its teardown unregisters it.
/// </para>
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class MobileShellDiscoveryAfterConnectTests : IDisposable
{
    /// <summary>
    /// How long to wait on the sweep task, which runs on the thread pool. Generous because
    /// nothing here is timing-sensitive: the sweep enters its scope as its first statement, and a
    /// cancelled sweep leaves it within one 3 s discovery window.
    /// </summary>
    private static readonly TimeSpan SweepWait = TimeSpan.FromSeconds(20);

    private const int AnalogPorts = 16;

    private readonly CapturingTransport _transport = new();
    private readonly CoreStreamingDevice _core;
    private readonly ShellTestDevice _device;
    private readonly MobileShellViewModel _shell = new();
    private readonly CountingDiscoveryScope _scope = new();
    private readonly INetworkDiscoveryScope? _previousScope = NetworkDiscoveryScope.Current;

    public MobileShellDiscoveryAfterConnectTests()
    {
        NetworkDiscoveryScope.Current = _scope;

        _core = new CoreStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _core.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = AnalogPorts,
            AnalogInRes = 65535
        });

        _device = new ShellTestDevice("Discovery test device");
        _device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };
        _device.AttachCore(_core);
        _device.SyncFromCore(_core);
    }

    public void Dispose()
    {
        _shell.Dispose();
        NetworkDiscoveryScope.Current = _previousScope;
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();
    }

    /// <summary>
    /// The defect. The sweep ran for the rest of the session after a connect, so the button went
    /// on reading "Stop scanning" over a connected device.
    /// </summary>
    [Fact]
    public void A_successful_connect_ends_the_discovery_sweep()
    {
        _shell.ScanCommand.Execute(null);
        Assert.True(_shell.IsScanning, "Arrangement: the scan did not start.");

        _device.AdoptInto(_shell);

        Assert.True(_shell.IsConnected);
        Assert.False(_shell.IsScanning, "The sweep was still running after a successful connect.");
    }

    /// <summary>
    /// The resource half, and the one the issue measured: the scope the sweep holds — Android's
    /// multicast lock — is released by the connect, not left held until the user thinks to tap
    /// "Stop scanning".
    /// </summary>
    [Fact]
    public void A_successful_connect_releases_the_discovery_scope()
    {
        _shell.ScanCommand.Execute(null);
        Assert.True(
            WaitUntil(() => _scope.Entered > 0),
            "Arrangement: the sweep never entered the discovery scope.");

        _device.AdoptInto(_shell);

        Assert.True(
            WaitUntil(() => _scope.Disposed == _scope.Entered),
            $"The connect left {_scope.Entered - _scope.Disposed} discovery scope(s) held.");
    }

    /// <summary>
    /// Re-scanning stays available: the fix ends the sweep, it does not forbid starting another
    /// one — a user who wants to switch devices taps Scan again and gets a fresh sweep, holding
    /// the scope again for as long as that one runs.
    /// </summary>
    [Fact]
    public void Scanning_can_be_started_again_while_a_device_is_connected()
    {
        _shell.ScanCommand.Execute(null);
        _device.AdoptInto(_shell);
        Assert.False(_shell.IsScanning);
        // Against the count as it stands now, not against zero: a sweep cancelled before the
        // thread pool ever ran it never entered the scope at all, so the first one may have
        // contributed nothing to count.
        var enteredBeforeRescan = _scope.Entered;

        _shell.ScanCommand.Execute(null);

        Assert.True(_shell.IsScanning, "A connected device blocked a new scan.");
        Assert.True(
            WaitUntil(() => _scope.Entered > enteredBeforeRescan),
            "The new sweep never entered the discovery scope.");
    }

    /// <summary>
    /// A connect that does NOT succeed must leave the user able to pick again: the sweep keeps
    /// running and the device list keeps filling. Driven through the manual-IP route, whose
    /// rejection is reached without a transport.
    /// </summary>
    [Fact]
    public void A_connect_that_never_succeeds_leaves_the_sweep_running()
    {
        _shell.ScanCommand.Execute(null);
        _shell.ManualIp = "not-an-ip-address";

        _shell.ManualConnectCommand.Execute(null);

        Assert.False(_shell.IsConnected);
        Assert.True(_shell.IsScanning, "A refused connect stopped discovery and stranded the user.");
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + SweepWait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) { return true; }
            Thread.Sleep(25);
        }

        return condition();
    }

    /// <summary>
    /// Stands in for the platform discovery scope — on Android, the one that takes and releases
    /// the <c>WifiManager.MulticastLock</c> — and counts how many have been entered and how many
    /// of those released.
    /// </summary>
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
                // Idempotent: a double dispose must not make the books look balanced when they
                // are not, nor unbalanced when they are.
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    Interlocked.Increment(ref owner._disposed);
                }
            }
        }
    }
}
