using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Daqifi.Core.Device;
using Daqifi.Desktop.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Issue #696: a device that stops streaming lets the computer sleep again, on every path that
/// ends the stream.
/// </summary>
/// <remarks>
/// <para>
/// On Windows, a device that starts to stream asks the computer not to sleep. Before this fix,
/// only <c>StopStreaming</c> cancelled the request, and only for a device that it found streaming.
/// A drop sets <c>IsStreaming</c> to false first, so the teardown <c>Disconnect</c> found nothing
/// to stop, and the computer could not sleep until the app closed.
/// </para>
/// <para>
/// The request goes through <see cref="IKeepAwake"/>, so these tests run on any platform. The
/// device and its Core device are real (<see cref="CoreDecodingHarness"/>), and the drop is a real
/// one: the transport reports that it closed, and Core reports <see cref="ConnectionStatus.Lost"/>.
/// </para>
/// </remarks>
public sealed class KeepAwakeReleaseTests : IDisposable
{
    private readonly CoreDecodingHarness _harness = CoreDecodingHarness.Create(analogPorts: 1);
    private readonly RecordingKeepAwake _keepAwake = new();

    public KeepAwakeReleaseTests()
    {
        _harness.Device.KeepAwake = _keepAwake;
        _harness.EnableAnalog(0);
    }

    public void Dispose() => _harness.Dispose();

    /// <summary>The control: a stream that starts holds the computer awake, one time.</summary>
    [Fact]
    public void A_stream_that_starts_keeps_the_computer_awake()
    {
        _harness.Device.InitializeStreaming();

        Assert.True(_harness.Device.IsStreaming);
        Assert.Equal(1, _keepAwake.Acquired);
        Assert.Equal(0, _keepAwake.Released);
    }

    [Fact]
    public void A_normal_stop_lets_the_computer_sleep()
    {
        _harness.Device.InitializeStreaming();

        _harness.Device.StopStreaming();

        Assert.Equal(0, _keepAwake.Held);
    }

    /// <summary>
    /// The defect. Core reports the drop on its transport thread, and the device marks the stream
    /// inactive there. Nothing else ends the stream, so the request must end there too.
    /// </summary>
    [Fact]
    public void A_drop_while_streaming_lets_the_computer_sleep()
    {
        _harness.Device.InitializeStreaming();

        _harness.Transport.Disconnect();

        // The premise: the drop was real, and the device saw it.
        Assert.Equal(ConnectionStatus.Lost, _harness.Core.Status);
        Assert.False(_harness.Device.IsStreaming);
        Assert.Equal(0, _keepAwake.Held);
    }

    /// <summary>
    /// What the app does after a drop: <c>ConnectionManager</c> disconnects the device. The
    /// request ends one time, not one time for each path.
    /// </summary>
    [Fact]
    public void A_drop_and_then_the_teardown_disconnect_release_it_one_time()
    {
        _harness.Device.InitializeStreaming();

        _harness.Transport.Disconnect();
        _harness.Device.Disconnect();

        Assert.Equal(1, _keepAwake.Acquired);
        Assert.Equal(1, _keepAwake.Released);
    }

    [Fact]
    public void A_disconnect_while_streaming_lets_the_computer_sleep()
    {
        _harness.Device.InitializeStreaming();

        _harness.Device.Disconnect();

        Assert.Equal(1, _keepAwake.Acquired);
        Assert.Equal(1, _keepAwake.Released);
    }

    /// <summary>
    /// <c>Connect</c> first removes the Core device it had, and the stream on that Core device
    /// ends with it.
    /// </summary>
    [Fact]
    public void A_connect_that_replaces_a_streaming_Core_device_lets_the_computer_sleep()
    {
        _harness.Device.InitializeStreaming();

        // The probe creates no new Core device, so this connect fails after it removes the old one.
        Assert.False(_harness.Device.Connect());

        Assert.Equal(0, _keepAwake.Held);
    }

    /// <summary>
    /// The paths overlap: a stop, then the disconnect that stops again. Each request ends one
    /// time, so a second device's request is not cancelled by this device's extra release.
    /// </summary>
    [Fact]
    public void Every_end_path_together_releases_it_one_time()
    {
        _harness.Device.InitializeStreaming();

        _harness.Device.StopStreaming();
        _harness.Device.StopStreaming();
        _harness.Transport.Disconnect();
        _harness.Device.Disconnect();

        Assert.Equal(1, _keepAwake.Acquired);
        Assert.Equal(1, _keepAwake.Released);
    }

    /// <summary>A second stream on the same device is a second request.</summary>
    [Fact]
    public void A_restart_after_a_stop_keeps_the_computer_awake_again()
    {
        _harness.Device.InitializeStreaming();
        _harness.Device.StopStreaming();

        _harness.Device.InitializeStreaming();

        Assert.Equal(2, _keepAwake.Acquired);
        Assert.Equal(1, _keepAwake.Released);
    }

    /// <summary>
    /// Review on PR #697. A drop occurs while the start is between its decision to hold a request
    /// and the request itself. The drop must not release first, or the owner ignores that release
    /// (nothing is held yet) and then keeps the late request for good.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner is the real <see cref="KeepAwakeOwner"/>, with a recording function in place of
    /// the Win32 call. <see cref="PausingKeepAwake"/> stops the start at the moment it asks the
    /// owner, which is after the device recorded that it holds a request.
    /// </para>
    /// <para>
    /// The drop runs on a second thread, as Core's transport thread does. Without serialization it
    /// runs to its end while the start is stopped. With serialization it waits for the start, so
    /// the test gives it <see cref="DropHeadStart"/> to get to that wait, and then lets the start
    /// go on. Either order must end with the state cleared.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_drop_while_the_start_asks_for_its_request_still_lets_the_computer_sleep()
    {
        var applied = new BlockingCollection<bool>();
        using var owner = new KeepAwakeOwner(applied.Add);
        using var pausing = new PausingKeepAwake(owner);
        _harness.Device.KeepAwake = pausing;

        var starter = StartThread(_harness.Device.InitializeStreaming, out var startFailure);
        Assert.True(pausing.Paused.Wait(Timeout), "The start did not get to its keep-awake request.");

        var dropper = StartThread(_harness.Transport.Disconnect, out var dropFailure);
        dropper.Join(DropHeadStart);

        pausing.Resume.Set();
        Assert.True(starter.Join(Timeout), "The start did not finish.");
        Assert.True(dropper.Join(Timeout), "The drop did not finish.");
        Assert.Null(startFailure.Value);
        Assert.Null(dropFailure.Value);

        // The premise: the drop was real, and the device saw it.
        Assert.Equal(ConnectionStatus.Lost, _harness.Core.Status);
        Assert.False(_harness.Device.IsStreaming);

        // What ConnectionManager does next.
        _harness.Device.Disconnect();

        Assert.Equal([true, false], AppliedUntilCleared(applied));

        // And the owner holds nothing: a new stream sets the state again.
        owner.Acquire();
        Assert.True(applied.TryTake(out var next, Timeout), "A new stream did not set the state.");
        Assert.True(next);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>How long the drop gets to run, or to get to its wait, before the start goes on.</summary>
    private static readonly TimeSpan DropHeadStart = TimeSpan.FromSeconds(1);

    /// <summary>Runs <paramref name="body"/> on a new thread, and keeps what it throws.</summary>
    private static Thread StartThread(Action body, out StrongBox<Exception?> failure)
    {
        var box = new StrongBox<Exception?>();
        failure = box;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                box.Value = ex;
            }
        });
        thread.Start();
        return thread;
    }

    /// <summary>
    /// The states the owner applied, up to and including the first that clears it. The owner
    /// applies them on its own thread, so this waits for them.
    /// </summary>
    private static List<bool> AppliedUntilCleared(BlockingCollection<bool> applied)
    {
        var states = new List<bool>();
        while (true)
        {
            Assert.True(
                applied.TryTake(out var state, Timeout),
                $"The owner did not clear the state within {Timeout.TotalSeconds} s. " +
                $"Applied so far: [{string.Join(", ", states)}]");
            states.Add(state);

            if (!state)
            {
                return states;
            }
        }
    }

    /// <summary>
    /// Stops the first <see cref="Acquire"/> until <see cref="Resume"/> is set, then passes every
    /// call on to the real owner.
    /// </summary>
    private sealed class PausingKeepAwake(IKeepAwake inner) : IKeepAwake, IDisposable
    {
        private int _acquires;

        public ManualResetEventSlim Paused { get; } = new();

        public ManualResetEventSlim Resume { get; } = new();

        public void Acquire()
        {
            if (Interlocked.Increment(ref _acquires) == 1)
            {
                Paused.Set();
                Resume.Wait(Timeout);
            }

            inner.Acquire();
        }

        public void Release() => inner.Release();

        public void Dispose()
        {
            Paused.Dispose();
            Resume.Dispose();
        }
    }

    /// <summary>Counts the calls. Thread-safe, because Core reports a drop on its own thread.</summary>
    private sealed class RecordingKeepAwake : IKeepAwake
    {
        private int _acquired;
        private int _released;

        public int Acquired => Volatile.Read(ref _acquired);

        public int Released => Volatile.Read(ref _released);

        /// <summary>Requests not yet released.</summary>
        public int Held => Acquired - Released;

        public void Acquire() => Interlocked.Increment(ref _acquired);

        public void Release() => Interlocked.Increment(ref _released);
    }
}
