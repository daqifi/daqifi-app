using Daqifi.Core.Device;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Logger;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Issue #295: a streaming frame's pre-scaled analog floats reached the app unvalidated, and a
/// single non-finite one used to make the Log Summary flyout's Average read <c>NaN</c> for the
/// rest of the session.
///
/// <para>
/// The frames below carry <c>AnalogInDataFloat</c> because that is the payload with no upstream
/// validation — the integer payload is scaled through Core, which sanitizes the coefficients it
/// scales with. Which payload a board actually sends is a firmware decision (the bench board at
/// fw 3.7.2 sends the integer one over USB), which is why the guard is placed after the branch
/// rather than inside the float leg.
/// </para>
///
/// <para>
/// Core's per-channel statistics keep a running <c>ValueSum</c>, so the poisoning is permanent
/// rather than momentary — and <c>Min</c> and <c>Max</c> keep working, because every comparison
/// against a NaN is false. One column of one row goes wrong and everything beside it keeps
/// reading correctly, which is exactly what makes it look like a rendering fault rather than a
/// bad reading. That asymmetry is pinned below, because it is the reason the bug was invisible.
/// </para>
///
/// <para>
/// These tests drive the <em>real</em> decode path: a protobuf frame goes in through Core's own
/// stream decode, the one the transport feeds, and what comes out the far end is read off
/// the channel and off a <see cref="SummaryLogger"/> subscribed to it the way
/// <c>LoggingManager.HandleChannelUpdate</c> subscribes the app's real loggers. Nothing about the
/// guard is simulated.
/// </para>
/// </summary>
public class NonFiniteStreamedReadingTests
{
    /// <summary>
    /// The app wrapper over a real Core device, with the frames handed to Core.
    /// </summary>
    /// <remarks>
    /// Until issue #440 these tests pushed a frame into the wrapper's own decode. The wrapper no
    /// longer has one: Core decodes the frame and the wrapper publishes Core's samples, so the
    /// frame now goes to Core (<see cref="CoreDecodingHarness"/>) and the guard is exercised where
    /// it now lives, on the sample Core hands over.
    /// </remarks>
    private sealed class StreamingProbe : IDisposable
    {
        private readonly CoreDecodingHarness _harness = CoreDecodingHarness.Create(analogPorts: 3);

        internal CoreDecodingHarness.ProbeDevice Device => _harness.Device;

        internal IEnumerable<IChannel> DataChannels => Device.DataChannels.Where(c => c.IsActive);

        internal Action<DeviceMessage>? DeviceMessageDispatched
        {
            set => Device.DeviceMessageDispatched = value;
        }

        internal AnalogChannel AddActiveAnalogChannel(int index) => _harness.EnableAnalog(index);

        /// <summary>
        /// Delivers one streaming frame carrying firmware-supplied pre-scaled floats, one value
        /// per active analog channel in index order. The first frame starts the session, after the
        /// channels are switched on, as the LOGGING toggle does.
        /// </summary>
        internal void ReceiveFloatFrame(uint deviceTicks, params float[] preScaledVolts)
        {
            if (!Device.IsStreaming)
            {
                Device.InitializeStreaming();
            }

            _harness.Core.Feed(CoreDecodingHarness.FloatFrame(
                CoreDecodingHarness.FirstCounter + deviceTicks, preScaledVolts));
        }

        /// <summary>
        /// Delivers a frame that carries no analog readings — only the DIO port, and no digital
        /// channel is on — so the device dispatches one more device message and nothing else.
        /// </summary>
        internal void ReceiveFrameWithNoReadings(uint deviceTicks)
        {
            var frame = CoreDecodingHarness.FloatFrame(CoreDecodingHarness.FirstCounter + deviceTicks);
            frame.DigitalData = Google.Protobuf.ByteString.CopyFrom(0);
            _harness.Core.Feed(frame);
        }

        internal long DiscardedNonFiniteReadings(int channelIndex) =>
            Device.DiscardedNonFiniteReadings(channelIndex);

        public void Dispose() => _harness.Dispose();
    }

    /// <summary>
    /// A summary logger fed from the device the way the app feeds it, on both legs: samples arrive
    /// via <c>AbstractChannel.OnChannelUpdated</c> (which <c>LoggingManager.HandleChannelUpdate</c>
    /// subscribes to and fans out from), and each frame's <see cref="DeviceMessage"/> arrives via
    /// the dispatch the device makes for every frame. Republishing on every device message so a
    /// test does not have to send a thousand frames to see the panel refresh.
    /// </summary>
    private static SummaryLogger SummaryFedBy(StreamingProbe device)
    {
        var summary = new SummaryLogger { Enabled = true, SampleSize = 1 };

        foreach (var channel in device.DataChannels)
        {
            channel.OnChannelUpdated += (_, sample) => summary.Log(sample);
        }

        device.DeviceMessageDispatched = summary.Log;
        return summary;
    }

    /// <summary>
    /// The single channel row the flyout would render for the single reporting device, once the
    /// panel has refreshed after the last frame <paramref name="deviceTicks"/> precedes.
    /// </summary>
    /// <remarks>
    /// Core raises a frame's raw message before it decodes that frame's samples, so a refresh on a
    /// frame's device message covers the frames before it (issue #440). One more frame, with no
    /// readings, makes the refresh cover every frame the test sent.
    /// </remarks>
    private static ChannelAcquisitionStatistics TheRow(SummaryLogger summary, StreamingProbe device, uint deviceTicks)
    {
        device.ReceiveFrameWithNoReadings(deviceTicks);
        return Assert.Single(Assert.Single(summary.Devices).Snapshot.Channels);
    }

    /// <summary>
    /// The value the firmware sent is not a measurement, so it does not become the channel's
    /// reading. Nothing downstream ever sees it — this is the whole of the fix.
    /// </summary>
    [Fact]
    public void A_non_finite_reading_never_becomes_the_channel_s_value()
    {
        using var device = new StreamingProbe();
        var channel = device.AddActiveAnalogChannel(0);

        device.ReceiveFloatFrame(1_000, float.NaN);

        Assert.Null(channel.ActiveSample);
    }

    /// <summary>
    /// An infinity is refused for the same reason a NaN is: it poisons a running sum just as
    /// thoroughly and is no more a voltage.
    /// </summary>
    /// <remarks>
    /// Deliberately a different answer from <c>SessionSampleWriter</c>'s, which refuses only NaN
    /// because its question is what SQLite can store — and SQLite stores infinities fine. Two
    /// boundaries, two questions, two answers.
    /// </remarks>
    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void An_infinite_reading_is_refused_like_a_NaN(float reading)
    {
        using var device = new StreamingProbe();
        var channel = device.AddActiveAnalogChannel(0);

        device.ReceiveFloatFrame(1_000, reading);

        Assert.Null(channel.ActiveSample);
    }

    /// <summary>
    /// One bad value costs one reading on one channel — not the frame, and not its neighbours.
    /// A guard that dropped the whole frame would turn a single broken channel into a device that
    /// records nothing.
    /// </summary>
    [Fact]
    public void A_non_finite_reading_costs_its_neighbours_in_the_same_frame_nothing()
    {
        using var device = new StreamingProbe();
        var first = device.AddActiveAnalogChannel(0);
        var broken = device.AddActiveAnalogChannel(1);
        var last = device.AddActiveAnalogChannel(2);

        device.ReceiveFloatFrame(1_000, 1.5f, float.NaN, 2.5f);

        Assert.Equal(1.5, first.ActiveSample!.Value, precision: 5);
        Assert.Null(broken.ActiveSample);
        Assert.Equal(2.5, last.ActiveSample!.Value, precision: 5);
    }

    /// <summary>
    /// A channel is refused only for as long as its readings are unusable. The frame after the
    /// bad one lands normally — the channel is not latched off.
    /// </summary>
    [Fact]
    public void A_channel_reports_again_the_moment_its_readings_do()
    {
        using var device = new StreamingProbe();
        var channel = device.AddActiveAnalogChannel(0);

        device.ReceiveFloatFrame(1_000, float.NaN);
        device.ReceiveFloatFrame(2_000, 3.25f);

        Assert.Equal(3.25, channel.ActiveSample!.Value, precision: 5);
    }

    /// <summary>
    /// The user-visible half, and the reason this issue exists: before the guard, the Average
    /// column of a channel that was otherwise reading perfectly went to <c>NaN</c> on the first
    /// bad frame and stayed there for the rest of the run.
    /// </summary>
    [Fact]
    public void The_summary_average_survives_a_non_finite_reading()
    {
        using var device = new StreamingProbe();
        var channel = device.AddActiveAnalogChannel(0);
        var summary = SummaryFedBy(device);

        device.ReceiveFloatFrame(1_000, 2.0f);
        device.ReceiveFloatFrame(2_000, float.NaN);
        device.ReceiveFloatFrame(3_000, 4.0f);

        var row = TheRow(summary, device, 4_000);

        Assert.Equal(3.0, row.MeanValue, precision: 5);
        Assert.Equal(2, row.SampleCount);
    }

    /// <summary>
    /// Why the bug read as a rendering fault. Min and Max were never wrong — Core compares with
    /// <c>&lt;</c> and <c>&gt;</c>, and every comparison against a NaN is false — so the row kept
    /// three correct numbers beside the one that had gone permanently wrong.
    /// </summary>
    [Fact]
    public void The_extremes_beside_the_average_stay_correct_too()
    {
        using var device = new StreamingProbe();
        var channel = device.AddActiveAnalogChannel(0);
        var summary = SummaryFedBy(device);

        device.ReceiveFloatFrame(1_000, 2.0f);
        device.ReceiveFloatFrame(2_000, float.NaN);
        device.ReceiveFloatFrame(3_000, 4.0f);

        var row = TheRow(summary, device, 4_000);

        Assert.Equal(2.0, row.MinValue, precision: 5);
        Assert.Equal(4.0, row.MaxValue, precision: 5);
    }

    /// <summary>
    /// The regression guard: refusing non-finite readings must not cost a good one. This one
    /// passes without the fix, and is here so a future widening of the guard cannot go unnoticed.
    /// </summary>
    [Fact]
    public void A_finite_reading_still_reaches_the_channel_and_the_summary()
    {
        using var device = new StreamingProbe();
        var channel = device.AddActiveAnalogChannel(0);
        var summary = SummaryFedBy(device);

        device.ReceiveFloatFrame(1_000, 1.25f);

        Assert.Equal(1.25, channel.ActiveSample!.Value, precision: 5);
        Assert.Equal(1.25, TheRow(summary, device, 2_000).MeanValue, precision: 5);
    }

    /// <summary>
    /// The throttled warning counts discards <em>per channel</em>, so a channel that starts
    /// failing later still gets a first line of its own instead of being swallowed by a shared
    /// count that has already passed its next power of ten.
    /// </summary>
    /// <remarks>
    /// This reads the tally the warning's occurrence number is drawn from rather than the log
    /// line, because <c>AppLogger</c> is a process-wide singleton that discards its NLog logger in
    /// test mode — there is nothing to assert against. What the tally cannot show is that the line
    /// was written; what it can show, and what matters to the design, is that the bookkeeping is
    /// per channel.
    /// </remarks>
    [Fact]
    public void Each_channel_keeps_its_own_tally_of_discarded_readings()
    {
        using var device = new StreamingProbe();
        device.AddActiveAnalogChannel(0);
        device.AddActiveAnalogChannel(1);

        device.ReceiveFloatFrame(1_000, float.NaN, 1.0f);
        device.ReceiveFloatFrame(2_000, float.NaN, 1.0f);
        device.ReceiveFloatFrame(3_000, float.NaN, float.PositiveInfinity);

        Assert.Equal(3, device.DiscardedNonFiniteReadings(0));
        Assert.Equal(1, device.DiscardedNonFiniteReadings(1));
    }

    /// <summary>
    /// A channel that has never produced a bad reading has nothing to report, so the throttle
    /// starts every channel at occurrence 1 rather than inheriting another channel's count.
    /// </summary>
    [Fact]
    public void A_channel_with_no_bad_readings_has_nothing_tallied()
    {
        using var device = new StreamingProbe();
        device.AddActiveAnalogChannel(0);

        device.ReceiveFloatFrame(1_000, 1.0f);

        Assert.Equal(0, device.DiscardedNonFiniteReadings(0));
    }

    /// <summary>
    /// The throttle logs the first ten discards on a channel in full and only then thins to
    /// powers of ten.
    /// </summary>
    /// <remarks>
    /// The opening band is what keeps the accepted reset race from costing a channel its first
    /// line altogether. A handler that was already past the <c>IsStreaming</c> guard when a
    /// restart cleared the tally can increment afterwards, so the new session's first real
    /// discard can land as occurrence 2 — which powers of ten alone would have swallowed until
    /// occurrence 10. Narrowing this back to <c>IsPowerOfTen</c> reintroduces exactly that, and
    /// this is the test that stops it.
    /// </remarks>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(1_000, true)]
    public void The_first_ten_discards_are_reported_and_then_only_powers_of_ten(long occurrence, bool reported)
    {
        Assert.Equal(reported, AbstractStreamingDevice.IsReportedDiscardOccurrence(occurrence));
    }
}
