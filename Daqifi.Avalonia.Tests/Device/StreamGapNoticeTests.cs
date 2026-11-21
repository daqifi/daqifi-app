using System.Globalization;
using System.Reflection;
using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Core.Device;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.DialogService;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Issue #595: when frames are lost in a stream, the user is told. Core finds the gap
/// (<c>DaqifiStreamingDevice.GapDetected</c>); the device counts it and raises
/// <see cref="AbstractStreamingDevice.StreamGapDetected"/> for the first gap of a session; the bell
/// shows one notice. Before this, the gap only broke the live graph's line, and the session closed
/// as a normal session.
/// </summary>
/// <remarks>
/// <para>
/// Every frame goes through Core's real decode (<see cref="CoreDecodingHarness"/>), so the gap is
/// the one Core finds.
/// </para>
/// <para>
/// <c>DaqifiViewModel.OnStreamGapDetected</c> only posts to the UI thread, which this test host
/// cannot pump, so the bell tests call the method it posts, with the text the event carried. That
/// the app subscribes at all is shown on the bench (HeadlessBench <c>--hold-reader</c>), not here.
/// </para>
/// <para>
/// On the app-host collection because the bell tests build a <see cref="DaqifiViewModel"/>.
/// </para>
/// </remarks>
[Collection(AppHostCollection.Name)]
public sealed class StreamGapNoticeTests : IDisposable
{
    private readonly CoreDecodingHarness _harness = CoreDecodingHarness.Create(analogPorts: 1);

    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// Two gaps in one session: frames at periods 4 and 5 are lost, then 10 to 13. Each gap is
    /// counted, and one notice is raised, for the first gap, with Core's facts about it.
    /// </summary>
    [Fact]
    public void A_session_with_gaps_raises_one_notice_and_counts_every_gap()
    {
        _harness.EnableAnalog(0);
        var gapsCoreFound = new List<TimestampGapEventArgs>();
        _harness.Core.GapDetected += (_, e) => gapsCoreFound.Add(e);
        _harness.Device.InitializeStreaming();

        var notices = NoticesRaisedWhile(() => Feed(0, 1, 2, 3, 6, 7, 8, 9, 14, 15, 16));

        Assert.Equal(2, gapsCoreFound.Count);
        Assert.Equal(2, _harness.Device.StreamGapCount);
        // 3 periods and 5 periods at 100 Hz.
        Assert.Equal(0.08, _harness.Device.StreamGapTime.TotalSeconds, precision: 6);
        var notice = Assert.Single(notices);
        Assert.Equal(
            AbstractStreamingDevice.DescribeStreamGap(_harness.Device.DeviceDisplayName, gapsCoreFound[0]),
            notice.Message);
    }

    /// <summary>The guard: a stream with no gap raises no notice and counts nothing.</summary>
    [Fact]
    public void A_steady_stream_raises_no_notice()
    {
        _harness.EnableAnalog(0);
        _harness.Device.InitializeStreaming();

        var notices = NoticesRaisedWhile(() => Feed(Enumerable.Range(0, 100).Select(i => (uint)i).ToArray()));

        Assert.Empty(notices);
        Assert.Equal(0, _harness.Device.StreamGapCount);
        Assert.Equal(TimeSpan.Zero, _harness.Device.StreamGapTime);
    }

    /// <summary>
    /// Each session has its own count and its own notice: a gap in the second session is said
    /// again, and the count starts from zero.
    /// </summary>
    [Fact]
    public void Each_session_gets_its_own_count_and_notice()
    {
        _harness.EnableAnalog(0);

        var notices = NoticesRaisedWhile(() =>
        {
            _harness.Device.InitializeStreaming();
            Feed(0, 1, 2, 3, 6, 7, 8, 9, 14);
            _harness.Device.StopStreaming();

            _harness.Device.InitializeStreaming();
            Feed(1000, 1001, 1002, 1003, 1007, 1008);
        });

        Assert.Equal(2, notices.Count);
        Assert.Equal(1, _harness.Device.StreamGapCount);
        // Only the second session's gap: 4 periods at 100 Hz.
        Assert.Equal(0.04, _harness.Device.StreamGapTime.TotalSeconds, precision: 6);
    }

    /// <summary>
    /// What the bell shows: a gap in a real Core stream gives one notice, owned by the device, so
    /// the per-device pruning takes it away when the device goes. A clean stream gives none.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_a_stream_with_a_gap_puts_a_notice_in_the_bell(bool withGap)
    {
        var viewModel = NewViewModel();
        // Attach replaced the probe's serial with Core's metadata, which has none.
        _harness.Device.DeviceSerialNo = "SN-PROBE";
        _harness.EnableAnalog(0);
        _harness.Device.InitializeStreaming();

        var periods = withGap ? new uint[] { 0, 1, 2, 3, 6, 7, 8, 9, 14 } : new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        foreach (var notice in NoticesRaisedWhile(() => Feed(periods)))
        {
            viewModel.AddStreamGapNotice(notice);
        }

        if (!withGap)
        {
            Assert.Empty(viewModel.NotificationList);
            return;
        }

        var shown = Assert.Single(viewModel.NotificationList);
        Assert.Equal("SN-PROBE", shown.DeviceSerialNo);
        Assert.Contains("samples are missing", shown.Message);

        // No device with that serial is connected, which is the state after it is unplugged.
        viewModel.RemoveNotification();
        Assert.Empty(viewModel.NotificationList);
    }

    /// <summary>
    /// Two devices with different serials and one friendly name: each gets its own notice, owned by
    /// its own serial (review of PR #663, which measured one notice for two such boards on the phone).
    /// </summary>
    [Fact]
    public void Two_devices_with_one_name_each_get_a_notice_with_their_own_serial()
    {
        using var other = CoreDecodingHarness.Create(analogPorts: 1);
        _harness.Device.DeviceSerialNo = "SN-A";
        other.Device.DeviceSerialNo = "SN-B";
        _harness.Device.FriendlyName = "Bench Nyquist";
        other.Device.FriendlyName = "Bench Nyquist";
        _harness.EnableAnalog(0);
        other.EnableAnalog(0);
        _harness.Device.InitializeStreaming();
        other.Device.InitializeStreaming();

        var notices = NoticesRaisedWhile(
            () =>
            {
                Feed(0, 1, 2, 3, 6, 7, 8);
                Feed(other, 0, 1, 2, 3, 6, 7, 8);
            },
            _harness.Device, other.Device);

        Assert.Equal(new[] { "SN-A", "SN-B" }, notices.Select(n => n.DeviceSerialNo).Order());
        Assert.All(notices, n => Assert.StartsWith("Bench Nyquist: samples are missing. ", n.Message));
    }

    /// <summary>
    /// The bell adds every first-gap notice, also when the text is the same: two devices with one
    /// name and a gap in the same second, or two sessions of one device with a gap in the same
    /// second. The device already raises a notice only once per device per session.
    /// </summary>
    [Fact]
    public void The_bell_keeps_every_first_gap_notice_even_with_the_same_text()
    {
        var viewModel = NewViewModel();
        const string text = "Bench Nyquist: samples are missing. The data stream from the device had its first gap at 14:03:12.";

        viewModel.AddStreamGapNotice(Notifications.ForDevice("SN-A", text));
        viewModel.AddStreamGapNotice(Notifications.ForDevice("SN-B", text));
        viewModel.AddStreamGapNotice(Notifications.ForDevice("SN-A", text));

        Assert.Equal(new[] { "SN-A", "SN-B", "SN-A" }, viewModel.NotificationList.Select(n => n.DeviceSerialNo));
        Assert.Equal(3, viewModel.NotificationCount);
    }

    [Fact]
    public void The_same_notice_for_the_same_device_is_not_stacked()
    {
        var viewModel = NewViewModel();

        viewModel.AddDeviceNoticeOnce("SN-1", "Nyquist: samples are missing.");
        viewModel.AddDeviceNoticeOnce("SN-1", "Nyquist: samples are missing.");
        viewModel.AddDeviceNoticeOnce("SN-2", "Nyquist: samples are missing.");

        Assert.Equal(2, viewModel.NotificationList.Count);
        Assert.Equal(2, viewModel.NotificationCount);
    }

    /// <summary>
    /// The text says when the first gap started: at the frame before it, so the gap frame's time
    /// less the interval. It gives no length (see <c>DescribeStreamGap</c>).
    /// </summary>
    [Theory]
    [InlineData(7.0, "The data stream from the device had its first gap at 14:03:12.")]
    [InlineData(0.03, "The data stream from the device had its first gap at 14:03:18.")]
    public void The_notice_says_when_the_first_gap_started(double seconds, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var gap = new TimestampGapEventArgs(new DateTime(2026, 11, 20, 14, 3, 19), seconds, 123);

            var text = AbstractStreamingDevice.DescribeStreamGap("Nyquist 1", gap);

            Assert.StartsWith("Nyquist 1: samples are missing. ", text);
            Assert.Contains(expected, text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// When the window closes, the desktop view model drops its subscription to each static notice
    /// event. A static event roots its subscribers, so without this the closed window's view model
    /// stays alive until the app exits (sweep review of PR #663).
    /// </summary>
    /// <remarks>
    /// The view model subscribes in its start-up block, which runs only in the app, so the test
    /// subscribes the same private handler and reads the event's invocation list.
    /// </remarks>
    [Theory]
    [InlineData(typeof(AbstractStreamingDevice), nameof(AbstractStreamingDevice.StreamGapDetected), "OnStreamGapDetected")]
    [InlineData(typeof(AbstractChannel), nameof(AbstractChannel.ScaleResultsRejected), "OnScaleResultsRejected")]
    public void Dispose_drops_the_view_models_static_notice_subscription(Type owner, string eventName, string handlerName)
    {
        var viewModel = NewViewModel();
        var notice = owner.GetEvent(eventName, BindingFlags.Public | BindingFlags.Static)!;
        var method = typeof(DaqifiViewModel).GetMethod(handlerName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handler = Delegate.CreateDelegate(notice.EventHandlerType!, viewModel, method);
        notice.AddEventHandler(null, handler);
        try
        {
            Assert.True(IsSubscribed(owner, eventName, viewModel), "control: the handler must be subscribed first");

            viewModel.Dispose();

            Assert.False(IsSubscribed(owner, eventName, viewModel));
        }
        finally
        {
            notice.RemoveEventHandler(null, handler);
        }
    }

    /// <summary>Whether a handler on <paramref name="target"/> is in the static event's list.</summary>
    private static bool IsSubscribed(Type owner, string eventName, object target) =>
        owner.GetField(eventName, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is Delegate list
        && list.GetInvocationList().Any(h => ReferenceEquals(h.Target, target));

    private static DaqifiViewModel NewViewModel()
    {
        Daqifi.Desktop.App.InitializeMobile();
        return new DaqifiViewModel(new NullDialogService());
    }

    /// <summary>
    /// The notices <see cref="AbstractStreamingDevice.StreamGapDetected"/> raised while
    /// <paramref name="act"/> ran, for <paramref name="devices"/> (by default this harness's device).
    /// The event is static, so other devices' notices (from tests that run in parallel) are
    /// filtered out.
    /// </summary>
    private List<Notifications> NoticesRaisedWhile(Action act, params AbstractStreamingDevice[] devices)
    {
        var watched = devices.Length > 0 ? devices : [_harness.Device];
        var raised = new List<Notifications>();
        void Handler(AbstractStreamingDevice source, Notifications notice)
        {
            if (watched.Any(device => ReferenceEquals(source, device)))
            {
                lock (raised) { raised.Add(notice); }
            }
        }

        AbstractStreamingDevice.StreamGapDetected += Handler;
        try
        {
            act();
        }
        finally
        {
            AbstractStreamingDevice.StreamGapDetected -= Handler;
        }

        return raised;
    }

    /// <summary>Feeds one one-channel frame at each of these whole sample periods from the start.</summary>
    private void Feed(params uint[] periodsFromStart) => Feed(_harness, periodsFromStart);

    private static void Feed(CoreDecodingHarness harness, params uint[] periodsFromStart)
    {
        foreach (var periods in periodsFromStart)
        {
            harness.Core.Feed(CoreDecodingHarness.FloatFrame(
                CoreDecodingHarness.FirstCounter + periods * harness.TicksPerSample, 1f));
        }
    }
}
