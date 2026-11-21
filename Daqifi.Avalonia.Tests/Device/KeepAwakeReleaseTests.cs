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
