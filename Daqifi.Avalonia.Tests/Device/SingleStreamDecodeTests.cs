using Daqifi.Core.Communication.Messages;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Google.Protobuf;
using Xunit;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Issue #440: every stream frame was decoded twice. Core decoded it and published per-channel
/// samples, and then <c>AbstractStreamingDevice</c> ran its own copy of the same pipeline on the
/// raw frame. The app now publishes Core's samples and decodes nothing itself.
/// </summary>
/// <remarks>
/// <para>
/// The copy also had its own leftover-frame gate, with a 2.5 <em>second</em> window where Core's
/// is 2.5 <em>sample periods</em>. On a stop and start inside 2.5 s, Core passed the first genuine
/// frame and the app's gate discarded it, and then the next 4, each one period after the last
/// discard. Measured on the bench Nq1 (fw 3.9.0, 100 Hz, a 1.16 s restart): 402 samples decoded by
/// Core, 397 published by the app. The restart tests below fail against the old decode.
/// </para>
/// <para>
/// Every frame here goes through Core's real decode (<see cref="CoreDecodingHarness"/>), so the
/// guards that the app's copy duplicated — the leftover gate and the first-frame hold — are the
/// ones that run.
/// </para>
/// </remarks>
public sealed class SingleStreamDecodeTests : IDisposable
{
    private readonly List<CoreDecodingHarness> _harnesses = [];

    public void Dispose()
    {
        foreach (var harness in _harnesses)
        {
            harness.Dispose();
        }
    }

    private CoreDecodingHarness Harness(uint analogPorts = 3, uint digitalPorts = 0, string firmware = "3.8.0")
    {
        var harness = CoreDecodingHarness.Create(analogPorts, digitalPorts, firmware);
        _harnesses.Add(harness);
        return harness;
    }

    /// <summary>Every sample the app channel published, in order.</summary>
    private static List<DataSample> Published(IChannel channel)
    {
        var samples = new List<DataSample>();
        channel.OnChannelUpdated += (_, sample) => samples.Add(sample);
        return samples;
    }

    /// <summary>Feeds <paramref name="count"/> one-channel frames at the session rate.</summary>
    /// <returns>The counter of the last frame fed.</returns>
    private static uint FeedFrames(CoreDecodingHarness harness, uint firstCounter, int count, float volts = 1.0f)
    {
        var counter = firstCounter;
        for (var i = 0; i < count; i++)
        {
            counter = unchecked(firstCounter + (uint)i * harness.TicksPerSample);
            harness.Core.Feed(CoreDecodingHarness.FloatFrame(counter, volts));
        }

        return counter;
    }

    [Fact]
    public void Each_channel_publishes_the_sample_Core_decoded()
    {
        var harness = Harness();
        var channels = new[] { harness.EnableAnalog(0), harness.EnableAnalog(1), harness.EnableAnalog(2) };
        harness.Device.InitializeStreaming();

        harness.Core.Feed(CoreDecodingHarness.RawFrame(CoreDecodingHarness.FirstCounter, 100, 30_000, 65_000));

        foreach (var channel in channels)
        {
            var coreSample = channel.CoreChannel.ActiveSample;
            Assert.NotNull(coreSample);
            Assert.NotNull(channel.ActiveSample);
            Assert.Equal(coreSample.Value, channel.ActiveSample.Value);
            Assert.Equal(coreSample.Timestamp.Ticks, channel.ActiveSample.TimestampTicks);
            Assert.Equal(channel.Name, channel.ActiveSample.ChannelName);
            Assert.Same(channel.CoreChannel, channel.ActiveSample.CoreChannel);
        }

        // The values are in channel order, scaled by each channel's calibration.
        Assert.Equal(channels[0].GetScaledValue(100), channels[0].ActiveSample!.Value);
        Assert.Equal(channels[2].GetScaledValue(65_000), channels[2].ActiveSample!.Value);
    }

    [Fact]
    public void A_restart_inside_two_and_a_half_seconds_keeps_its_first_samples()
    {
        var harness = Harness();
        var channel = harness.EnableAnalog(0);
        harness.Device.InitializeStreaming();
        var last = FeedFrames(harness, CoreDecodingHarness.FirstCounter, 10);
        harness.Device.StopStreaming();

        var published = Published(channel);
        harness.Device.InitializeStreaming();
        // A stop and start one second apart on the device clock: inside the old 2.5 s window.
        FeedFrames(harness, unchecked(last + CoreDecodingHarness.TicksPerSecond), 10);

        Assert.Equal(10, published.Count);
    }

    [Fact]
    public void A_leftover_frame_at_a_restart_is_still_kept_out()
    {
        var harness = Harness();
        var channel = harness.EnableAnalog(0);
        harness.Device.InitializeStreaming();
        var last = FeedFrames(harness, CoreDecodingHarness.FirstCounter, 10);
        harness.Device.StopStreaming();

        var published = Published(channel);
        harness.Device.InitializeStreaming();
        // The frame the device latched at the stop: one period after the last one seen.
        harness.Core.Feed(CoreDecodingHarness.FloatFrame(unchecked(last + harness.TicksPerSample), 9.0f));
        FeedFrames(harness, unchecked(last + CoreDecodingHarness.TicksPerSecond), 3, volts: 2.0f);

        Assert.Equal(3, published.Count);
        Assert.All(published, sample => Assert.Equal(2.0, sample.Value, precision: 5));
        Assert.Equal(1, harness.Core.DiscardedStreamFrameCount);
    }

    [Fact]
    public void A_latched_first_frame_on_firmware_that_latches_is_kept_out_of_the_first_session()
    {
        // Firmware below v3.6.0 can emit the last frame of a session from before the connection
        // first (nyquist#535). The app held and judged that frame itself until #440; Core does now.
        var harness = Harness(firmware: "3.5.0");
        var channel = harness.EnableAnalog(0);
        var published = Published(channel);
        harness.Device.InitializeStreaming();

        harness.Core.Feed(CoreDecodingHarness.FloatFrame(CoreDecodingHarness.FirstCounter, 9.0f));
        // The next frame is seconds later on the device clock, so the first was not of this session.
        FeedFrames(harness, CoreDecodingHarness.FirstCounter + 5 * CoreDecodingHarness.TicksPerSecond, 3, volts: 2.0f);

        // The second frame is held until the third shows it belongs to this session.
        Assert.Equal(3, published.Count);
        Assert.All(published, sample => Assert.Equal(2.0, sample.Value, precision: 5));
    }

    [Fact]
    public void The_first_frame_on_fixed_firmware_is_published_on_arrival()
    {
        var harness = Harness(firmware: "3.8.0");
        var channel = harness.EnableAnalog(0);
        var published = Published(channel);
        harness.Device.InitializeStreaming();

        harness.Core.Feed(CoreDecodingHarness.FloatFrame(CoreDecodingHarness.FirstCounter, 1.5f));

        Assert.Equal(1.5, Assert.Single(published).Value, precision: 5);
    }

    [Fact]
    public void The_firmware_delta_is_the_device_clock_interval_between_frames()
    {
        var harness = Harness();
        var first = harness.EnableAnalog(0);
        var second = harness.EnableAnalog(1);
        var firstSamples = Published(first);
        var secondSamples = Published(second);
        harness.Device.InitializeStreaming();

        var counter = CoreDecodingHarness.FirstCounter;
        harness.Core.Feed(CoreDecodingHarness.FloatFrame(counter, 1f, 2f));
        harness.Core.Feed(CoreDecodingHarness.FloatFrame(counter += harness.TicksPerSample, 1f, 2f));
        // Two periods: one frame lost on the way.
        harness.Core.Feed(CoreDecodingHarness.FloatFrame(counter += 2 * harness.TicksPerSample, 1f, 2f));

        Assert.Equal(new double?[] { null, 10.0, 20.0 }, firstSamples.Select(s => s.FirmwareDeltaMs is { } ms ? Math.Round(ms, 3) : (double?)null));
        Assert.Equal(
            firstSamples.Select(s => s.FirmwareDeltaMs),
            secondSamples.Select(s => s.FirmwareDeltaMs));
    }

    [Fact]
    public void The_firmware_delta_starts_afresh_with_each_session()
    {
        var harness = Harness();
        var channel = harness.EnableAnalog(0);
        harness.Device.InitializeStreaming();
        var last = FeedFrames(harness, CoreDecodingHarness.FirstCounter, 3);
        harness.Device.StopStreaming();

        var published = Published(channel);
        harness.Device.InitializeStreaming();
        FeedFrames(harness, unchecked(last + CoreDecodingHarness.TicksPerSecond), 2);

        Assert.Null(published[0].FirmwareDeltaMs);
        Assert.Equal(10.0, published[1].FirmwareDeltaMs!.Value, precision: 3);
    }

    [Fact]
    public void A_digital_output_channel_gets_no_streamed_sample()
    {
        var harness = Harness(analogPorts: 0, digitalPorts: 8);
        var input = harness.EnableDigital(0);
        var output = harness.EnableDigital(1);
        output.Direction = ChannelDirection.Output;
        harness.Device.InitializeStreaming();

        harness.Core.Feed(new DaqifiOutMessage
        {
            MsgTimeStamp = CoreDecodingHarness.FirstCounter,
            DigitalData = ByteString.CopyFrom(0b0000_0011)
        });

        Assert.Equal(1.0, input.ActiveSample!.Value);
        Assert.Null(output.ActiveSample);
    }

    [Fact]
    public void Every_frame_dispatches_one_device_message()
    {
        var harness = Harness();
        harness.EnableAnalog(0);
        harness.Device.InitializeStreaming();

        harness.Core.Feed(CoreDecodingHarness.FloatFrame(uint.MaxValue - harness.TicksPerSample, 1f));
        // The device counter wraps between these two frames.
        harness.Core.Feed(CoreDecodingHarness.FloatFrame(harness.TicksPerSample - 1, 1f));

        Assert.Equal(2, harness.Device.DeviceMessages.Count);
        Assert.Equal(new[] { false, true }, harness.Device.DeviceMessages.Select(m => m.Rollover));
    }

    /// <summary>
    /// Core raises a frame's raw message before it decodes the frame's samples. The device message
    /// must still follow its frame's samples and carry their timestamp, as it did when the app
    /// decoded the frame itself: the Log Summary refreshes on it.
    /// </summary>
    [Fact]
    public void Each_device_message_follows_its_frames_samples_and_carries_their_timestamp()
    {
        var harness = Harness();
        var first = harness.EnableAnalog(0);
        var second = harness.EnableAnalog(1);
        var samplesPublishedFirst = new List<bool>();
        harness.Device.DeviceMessageDispatched = message => samplesPublishedFirst.Add(
            first.ActiveSample?.TimestampTicks == message.TimestampTicks
            && second.ActiveSample?.TimestampTicks == message.TimestampTicks);
        harness.Device.InitializeStreaming();

        var counter = CoreDecodingHarness.FirstCounter;
        for (var i = 0; i < 3; i++)
        {
            harness.Core.Feed(CoreDecodingHarness.FloatFrame(counter, 1f, 2f));
            counter += harness.TicksPerSample;
        }

        // All three, without waiting for a fourth frame: the first is completed by the second
        // frame's arrival, the others by their own last sample.
        Assert.Equal(new[] { true, true, true }, samplesPublishedFirst);
    }

    [Fact]
    public void A_summary_that_refreshes_on_every_message_shows_every_reading()
    {
        var harness = Harness();
        var channel = harness.EnableAnalog(0);
        var summary = new SummaryLogger { Enabled = true, SampleSize = 1 };
        channel.OnChannelUpdated += (_, sample) => summary.Log(sample);
        harness.Device.DeviceMessageDispatched = summary.Log;
        harness.Device.InitializeStreaming();

        harness.Core.Feed(CoreDecodingHarness.FloatFrame(CoreDecodingHarness.FirstCounter, 2f));
        harness.Core.Feed(CoreDecodingHarness.FloatFrame(CoreDecodingHarness.FirstCounter + harness.TicksPerSample, 4f));

        var row = Assert.Single(Assert.Single(summary.Devices).Snapshot.Channels);
        Assert.Equal(2, row.SampleCount);
        Assert.Equal(3.0, row.MeanValue, precision: 5);
    }

    /// <summary>
    /// Two frames that arrive together but are one second apart on the device clock. The debug
    /// capture must say one second, as the samples do, not the time between their arrivals.
    /// </summary>
    [Fact]
    public void The_debug_capture_carries_the_decoded_frame_timestamp()
    {
        var harness = Harness();
        var channel = harness.EnableAnalog(0);
        var captured = new List<DateTime>();
        var published = Published(channel);
        harness.Device.SetDebugMode(true);
        harness.Device.DebugDataReceived += debug => captured.Add(debug.Timestamp);
        harness.Device.InitializeStreaming();

        harness.Core.Feed(CoreDecodingHarness.RawFrame(CoreDecodingHarness.FirstCounter, 1000));
        harness.Core.Feed(CoreDecodingHarness.RawFrame(
            CoreDecodingHarness.FirstCounter + CoreDecodingHarness.TicksPerSecond, 2000));

        Assert.Equal(published.Select(s => s.TimestampTicks), captured.Select(t => t.Ticks));
        Assert.Equal(TimeSpan.FromSeconds(1), captured[1] - captured[0]);
    }

    [Fact]
    public void The_last_frame_of_a_session_is_completed_at_the_stop()
    {
        var harness = Harness();
        harness.Device.InitializeStreaming();

        // No channel is on, so the frame has no sample to complete it, and no frame follows.
        harness.Core.Feed(new DaqifiOutMessage
        {
            MsgTimeStamp = CoreDecodingHarness.FirstCounter,
            DigitalData = ByteString.CopyFrom(0)
        });
        Assert.Empty(harness.Device.DeviceMessages);

        harness.Device.StopStreaming();

        Assert.Single(harness.Device.DeviceMessages);
    }

    [Fact]
    public void Nothing_is_published_while_the_device_is_not_streaming()
    {
        var harness = Harness();
        var channel = harness.EnableAnalog(0);
        var published = Published(channel);

        harness.Core.Feed(CoreDecodingHarness.FloatFrame(CoreDecodingHarness.FirstCounter, 1f));

        Assert.Empty(published);
        Assert.Empty(harness.Device.DeviceMessages);
    }

    [Fact]
    public void A_channel_list_resync_does_not_publish_a_frame_twice()
    {
        // Core repopulates its channels on every status message, and the wrapper resyncs its list
        // each time. The subscriptions must follow the list, not pile up on it.
        var harness = Harness();
        var channel = harness.EnableAnalog(0);
        harness.Core.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 3,
            AnalogInRes = 65535,
            TimestampFreq = CoreDecodingHarness.TicksPerSecond
        });
        harness.Device.Resync(harness.Core);
        channel.IsActive = true;
        var published = Published(channel);
        harness.Device.InitializeStreaming();

        FeedFrames(harness, CoreDecodingHarness.FirstCounter, 4);

        Assert.Equal(4, published.Count);
    }
}
