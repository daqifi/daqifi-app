using Daqifi.Avalonia.Tests.Device;
using Daqifi.Avalonia.Views;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Channel;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;
using CoreStreamingDevice = Daqifi.Core.Device.DaqifiStreamingDevice;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #358: the mobile shell's silent-stream watchdog. A device that stops delivering samples
/// while its socket stays open must be declared dead after 8 s of silence, so the user is not
/// left staring at "Streaming 16 channel(s) @ 100 Hz" over a frozen plot.
/// </summary>
/// <remarks>
/// Everything below the view-model is real: the app's device wrapper over a real Core
/// <c>DaqifiStreamingDevice</c>, a transport that accepts writes and never answers (which is
/// exactly what a silent device looks like from the app's side), and channels populated by Core's
/// own status handling. The render timer and the clock are the pieces replaced — the tests call
/// <see cref="MobileShellViewModel.PollActiveSamples"/> directly, and move a manual
/// <see cref="MobileShellViewModel.WatchdogClock"/> by one simulated tick before each poll.
/// <para>
/// In the <c>ConnectionManager</c> singleton collection because the shell's adopt step registers
/// the device with <c>ConnectionManager.Instance</c>, and its teardown unregisters it.
/// </para>
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class MobileShellSilentStreamWatchdogTests : IDisposable
{
    /// <summary>
    /// The 8 s watchdog window in polls of the render timer's normal 50 ms tick
    /// (<see cref="Tick"/>).
    /// </summary>
    private const int WatchdogPolls = 160;

    /// <summary>The render timer's tick in the foreground.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    private const int AnalogPorts = 16;

    private readonly CapturingTransport _transport = new();
    private readonly CoreStreamingDevice _core;
    private readonly ShellTestDevice _device;
    private readonly MobileShellViewModel _shell = new();
    private readonly ManualClock _clock = new();

    public MobileShellSilentStreamWatchdogTests()
    {
        _shell.WatchdogClock = _clock;
        // No dispatcher runs in the test project. Run the backstop timer's posted job inline.
        _shell.PostToUiThread = action => action();
        _core = new CoreStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _core.PopulateChannelsFromStatus(Status(enabledMask: null));

        _device = new ShellTestDevice("Watchdog test device");
        _device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };
        _device.AttachCore(_core);
        _device.SyncFromCore(_core);

        _device.AdoptInto(_shell);
        Assert.True(_shell.IsConnected);
        Assert.Equal(AnalogPorts, _shell.Channels.Count);
    }

    public void Dispose()
    {
        _shell.Dispose();
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();
    }

    [Fact]
    public void A_stream_that_delivers_nothing_is_declared_dead_once_the_window_elapses()
    {
        StartStreaming();

        Poll(WatchdogPolls - 1);
        Assert.True(_shell.IsStreaming, "The watchdog tripped before its window had elapsed.");

        Poll(1);

        Assert.False(_shell.IsStreaming);
        Assert.False(_shell.IsConnected);
        Assert.StartsWith("Lost connection to", _shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stream_that_keeps_delivering_is_never_declared_dead()
    {
        StartStreaming();
        var channel = ActiveAnalogChannels().First();

        for (var i = 0; i < WatchdogPolls * 3; i++)
        {
            channel.ActiveSample = new DataSample { Value = i, TimestampTicks = i + 1 };
            Poll(1);
        }

        Assert.True(_shell.IsStreaming);
        Assert.True(_shell.IsConnected);
        Assert.True(_shell.TotalSamples > 0);
    }

    /// <summary>
    /// #540 review: a scale expression with no valid result for any input (<c>x / 0</c>, or
    /// <c>Sqrt(x)</c> on a negative input) makes the channel drop every sample. The device is still
    /// sending data, so the watchdog must still see it arrive. It used to count only the samples
    /// the channel published to the loggers, saw none, and dropped a healthy connection after the
    /// window. The dropped samples must still never reach a logger.
    /// </summary>
    [Fact]
    public void A_stream_whose_every_sample_the_scale_expression_rejects_is_not_declared_dead()
    {
        StartStreaming();
        var channel = (AbstractChannel)ActiveAnalogChannels().First();
        channel.ScaleExpression = "x / 0";
        channel.IsScalingActive = true;
        Assert.True(channel.HasValidExpression, "Arrangement: the expression was not accepted.");
        var logged = new List<double>();
        channel.OnChannelUpdated += (_, sample) => logged.Add(sample.Value);

        const int samples = WatchdogPolls * 3;
        for (var i = 0; i < samples; i++)
        {
            channel.ActiveSample = new DataSample { Value = i + 1, TimestampTicks = i + 1 };
            Poll(1);
        }

        Assert.True(_shell.IsConnected, $"A device that kept sending data was disconnected ({_shell.Status}).");
        Assert.True(_shell.IsStreaming);
        Assert.Equal(samples, _shell.TotalSamples);
        Assert.Equal(samples, channel.RejectedScaleResultCount);
        Assert.Empty(logged);
    }

    /// <summary>
    /// The defect. A status frame arriving mid-stream carries the device's own enabled mask, and
    /// Core 1.8.0 resyncs every analog channel's <c>IsEnabled</c> from it IN PLACE — so the app's
    /// wrappers, which read <c>IsActive</c> straight through to Core, all report inactive. The
    /// shell's poll used to count zero monitored channels, take the "nothing to monitor" branch,
    /// and reset the silence counter on every tick: the watchdog was disarmed for the rest of the
    /// session while the screen still read "Streaming 16 channel(s)".
    /// </summary>
    /// <remarks>
    /// Driven through <c>PopulateChannelsFromStatus</c>, which is what Core's status handler calls
    /// for every status frame; a capturing transport cannot deliver a frame of its own.
    /// </remarks>
    [Fact]
    public void A_status_frame_that_clears_the_enabled_mask_mid_stream_does_not_disarm_the_watchdog()
    {
        StartStreaming();

        _core.PopulateChannelsFromStatus(Status(enabledMask: [0x00, 0x00]));

        // Guard the arrangement: if Core ever stops resyncing IsEnabled from the mask, this test
        // would pass without exercising the path it exists for.
        Assert.Empty(ActiveAnalogChannels());

        AssertStreamStoppedForNoEnabledChannels();
    }

    /// <summary>
    /// The same state reached from the app's side: every streamed channel deactivated under a
    /// running stream, the way the Channels and Profiles panes do it. Nothing is enabled, so the
    /// silence is explained — the stream is stopped and the user told why, but a transport that
    /// may be perfectly healthy is NOT torn down. That is the false positive the old
    /// reset-when-unmonitored gate existed to prevent, and it must stay prevented.
    /// </summary>
    [Fact]
    public void Deactivating_every_channel_mid_stream_stops_the_stream_but_keeps_the_connection()
    {
        StartStreaming();

        foreach (var channel in ActiveAnalogChannels())
        {
            _device.RemoveChannel(channel);
        }

        Assert.Empty(ActiveAnalogChannels());

        AssertStreamStoppedForNoEnabledChannels();
    }

    /// <summary>
    /// Silence banked while nothing was enabled is explained silence, and must not count toward
    /// declaring the transport dead once a channel comes back. Without this, a channel re-enabled
    /// late in the window tore the connection down on the very next poll, before the device had
    /// had a chance to send a single sample for it.
    /// </summary>
    [Fact]
    public void Silence_banked_with_no_channels_enabled_does_not_drop_the_connection_when_one_comes_back()
    {
        StartStreaming();
        var channels = ActiveAnalogChannels();
        foreach (var channel in channels)
        {
            _device.RemoveChannel(channel);
        }

        Poll(WatchdogPolls - 1);
        _device.AddChannel(channels[0]);
        Poll(1);

        Assert.True(_shell.IsConnected, "Silence banked while nothing was enabled dropped the connection.");
        Assert.True(_shell.IsStreaming);

        // The re-enabled channel still gets a full window of its own, and a device that really is
        // silent for all of it is still dropped.
        Poll(WatchdogPolls - 2);
        Assert.True(_shell.IsConnected, "The watchdog tripped before the re-enabled channel's window had elapsed.");
        Poll(1);
        Assert.False(_shell.IsConnected);
        Assert.StartsWith("Lost connection to", _shell.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// After the no-channels trip stops the stream, the user can start again on the same
    /// connection and gets a live, fully armed stream.
    /// </summary>
    [Fact]
    public void Streaming_restarts_cleanly_after_the_no_channels_trip()
    {
        StartStreaming();
        foreach (var channel in ActiveAnalogChannels())
        {
            _device.RemoveChannel(channel);
        }

        AssertStreamStoppedForNoEnabledChannels();

        StartStreaming();
        Assert.StartsWith($"Streaming {AnalogPorts} channel(s)", _shell.Status, StringComparison.Ordinal);

        Poll(WatchdogPolls);
        Assert.False(_shell.IsConnected, "The restarted stream's watchdog was not re-armed.");
    }

    /// <summary>
    /// Issue #601, the first hole. The watchdog counted render polls, and 160 polls is 8 s only at
    /// the full 50 ms tick. A dispatcher timer does not make up the ticks that it misses while the UI
    /// thread is busy. At one poll per second the window was 160 s, so a stream that delivered
    /// nothing for 45 s still read "Streaming". The window is time, so it must close at 8 s however
    /// few polls ran in it.
    /// </summary>
    [Fact]
    public void A_silent_stream_is_declared_dead_after_eight_seconds_even_when_polls_are_one_second_apart()
    {
        var slowTick = TimeSpan.FromSeconds(1);
        StartStreaming();

        Poll(7, slowTick);
        Assert.True(_shell.IsStreaming, "The watchdog tripped before 8 s had elapsed.");

        Poll(1, slowTick);

        Assert.False(_shell.IsStreaming, "No sample for 8 s, and the stream still reads as live.");
        Assert.False(_shell.IsConnected);
        Assert.StartsWith("Lost connection to", _shell.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #601, the third hole. The watchdog ran only when the view's render timer called the
    /// poll. If that timer stops, nothing judged the stream. The view-model's own backstop timer
    /// must run the poll and trip the watchdog with no render poll at all.
    /// </summary>
    [Fact]
    public void A_silent_stream_is_declared_dead_when_the_render_timer_stops_polling()
    {
        StartStreaming();

        for (var second = 1; second < 8; second++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            _clock.FireTimers();
        }
        Assert.True(_shell.IsStreaming, "The backstop tripped the watchdog before 8 s had elapsed.");

        _clock.Advance(TimeSpan.FromSeconds(1));
        _clock.FireTimers();

        Assert.False(_shell.IsStreaming, "With no render poll, a silent stream was never judged.");
        Assert.False(_shell.IsConnected);
        Assert.StartsWith("Lost connection to", _shell.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// The backstop lives exactly as long as the stream: one timer while streaming, none after a
    /// stop, and a restart does not leave the previous one running.
    /// </summary>
    [Fact]
    public void The_backstop_timer_lives_only_as_long_as_the_stream()
    {
        StartStreaming();
        Assert.Equal(1, _clock.LiveTimers);

        _shell.StreamToggleCommand.Execute(null);   // stop
        Assert.False(_shell.IsStreaming);
        Assert.Equal(0, _clock.LiveTimers);

        StartStreaming();
        Assert.Equal(1, _clock.LiveTimers);
    }

    /// <summary>
    /// While the render timer polls normally, the backstop must not judge the stream on its own.
    /// </summary>
    [Fact]
    public void The_backstop_does_not_trip_a_stream_that_the_render_timer_polls_and_that_delivers()
    {
        StartStreaming();
        var channel = ActiveAnalogChannels().First();

        for (var i = 0; i < WatchdogPolls * 3; i++)
        {
            channel.ActiveSample = new DataSample { Value = i, TimestampTicks = i + 1 };
            Poll(1);
            if (i % 20 == 0) { _clock.FireTimers(); }
        }

        Assert.True(_shell.IsStreaming);
        Assert.True(_shell.IsConnected);
    }

    /// <summary>
    /// The same slow tick must not make a live stream look dead: samples are counted on the receive
    /// thread, so a poll that runs late still sees every sample that arrived since the last one.
    /// </summary>
    [Fact]
    public void A_stream_that_keeps_delivering_is_not_declared_dead_when_polls_are_seconds_apart()
    {
        StartStreaming();
        var channel = ActiveAnalogChannels().First();

        for (var i = 0; i < 60; i++)
        {
            channel.ActiveSample = new DataSample { Value = i, TimestampTicks = i + 1 };
            Poll(1, TimeSpan.FromSeconds(5));
        }

        Assert.True(_shell.IsStreaming);
        Assert.True(_shell.IsConnected);
    }

    /// <summary>
    /// Issue #601, the second hole. A channel that comes back after a stretch with none active opens
    /// a fresh window. So a channel set that changes at least once per window — here every 3 s —
    /// held the watchdog open forever, and not one sample ever arrived. The fresh window must not
    /// move the ceiling: twice the window after the last sample, the watchdog trips.
    /// </summary>
    [Fact]
    public void Channels_that_keep_coming_back_cannot_hold_a_silent_stream_open()
    {
        StartStreaming();
        var channels = ActiveAnalogChannels();
        const int pollsPerPhase = 60;   // 3 s at the 50 ms tick
        const int ceilingPolls = WatchdogPolls * 2;

        for (var poll = 1; poll < ceilingPolls; poll++)
        {
            if (poll % pollsPerPhase == 0) { ToggleEveryChannel(channels); }
            Poll(1);
            Assert.True(_shell.IsStreaming, $"The watchdog tripped at poll {poll}, before the ceiling.");
        }

        Poll(1);

        Assert.False(_shell.IsStreaming, "Channels that kept coming back held a silent stream open past the ceiling.");
    }

    private void ToggleEveryChannel(List<IChannel> channels)
    {
        if (ActiveAnalogChannels().Count > 0)
        {
            foreach (var channel in channels) { _device.RemoveChannel(channel); }
        }
        else
        {
            foreach (var channel in channels) { _device.AddChannel(channel); }
        }
    }

    private void AssertStreamStoppedForNoEnabledChannels()
    {
        Assert.True(_shell.IsStreaming);

        Poll(WatchdogPolls - 1);
        Assert.True(_shell.IsStreaming, "The watchdog tripped before its window had elapsed.");

        Poll(1);

        Assert.False(_shell.IsStreaming, "The watchdog never fired after the channels went inactive.");
        Assert.True(_shell.IsConnected, "A stream with nothing enabled is not evidence of a dead transport.");
        Assert.StartsWith("Streaming stopped — no channels are enabled", _shell.Status, StringComparison.Ordinal);
    }

    private void StartStreaming()
    {
        _shell.StreamToggleCommand.Execute(null);
        Assert.True(_shell.IsStreaming, $"Arrangement: streaming did not start ({_shell.Status}).");
        Assert.Equal(AnalogPorts, ActiveAnalogChannels().Count);
        Assert.Equal(0, _shell.TotalSamples);
    }

    private void Poll(int count) => Poll(count, Tick);

    private void Poll(int count, TimeSpan interval)
    {
        for (var i = 0; i < count; i++)
        {
            _clock.Advance(interval);
            _shell.PollActiveSamples();
        }
    }

    private List<IChannel> ActiveAnalogChannels() =>
        _device.DataChannels
            .Where(c => c.Type == ChannelType.Analog && !c.IsOutput && c.IsActive)
            .ToList();

    private static DaqifiOutMessage Status(byte[]? enabledMask)
    {
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = AnalogPorts,
            AnalogInRes = 65535
        };
        if (enabledMask != null)
        {
            message.AnalogInPortEnabled = ByteString.CopyFrom(enabledMask);
        }

        return message;
    }

    /// <summary>
    /// A clock that moves only when the test moves it. Its timers fire only when the test calls
    /// <see cref="FireTimers"/>.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }

        public int LiveTimers => _timers.Count(t => !t.IsDisposed);

        public void FireTimers()
        {
            foreach (var timer in _timers.Where(t => !t.IsDisposed).ToList()) { timer.Fire(); }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool IsDisposed { get; private set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;

            public void Dispose() => IsDisposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
