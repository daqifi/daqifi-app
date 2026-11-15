using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins the live plot's redraw cadence and per-pixel decimation.
/// </summary>
/// <remarks>
/// Upstream WPF redrew the live plot once a second, which made the trace step visibly. The
/// cadence tests fail if that one-second throttle comes back. The decimation test fails if a live
/// series is built without OxyPlot's min/max decimator, which is what keeps a faster redraw
/// affordable at high sample rates.
/// </remarks>
public class LivePlotCadenceTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(32, 0, false)]   // the second 16 ms timer tick
    [InlineData(48, 0, true)]    // the third tick: about 20 fps
    [InlineData(100, 0, true)]   // false under the old 1 s throttle
    [InlineData(1048, 1000, true)]
    public void Redraw_is_due_on_every_third_timer_tick(long now, long last, bool expected) =>
        Assert.Equal(expected, PlotLogger.IsRedrawDue(now, last));

    [Fact]
    public void Redraw_runs_at_least_ten_times_a_second() =>
        Assert.True(PlotLogger.REDRAW_INTERVAL_MS <= 100,
            $"REDRAW_INTERVAL_MS is {PlotLogger.REDRAW_INTERVAL_MS} ms; the live plot should redraw at least 10 times a second.");

    /// <summary>
    /// The redraw budget (issue #595): a redraw that costs the UI thread C ms is followed by at least
    /// 3C ms of other work, between the 40 ms floor and the 250 ms ceiling. With 16 channels a redraw
    /// costs tens of milliseconds, and a fixed 20 fps then kept the UI thread busy most of the time.
    /// </summary>
    [Theory]
    [InlineData(0, 40)]       // nothing measured yet: the normal cadence
    [InlineData(10, 40)]      // a cheap redraw: still the normal cadence
    [InlineData(20, 60)]
    [InlineData(50, 150)]
    [InlineData(83.4, 250)]   // just past the ceiling: held to it
    [InlineData(5000, 250)]   // a pathological redraw still leaves the plot moving at 4 fps
    public void An_expensive_redraw_spaces_the_next_one(double costMs, long expectedIntervalMs) =>
        Assert.Equal(expectedIntervalMs, PlotLogger.RedrawIntervalMs(costMs));

    /// <summary>
    /// The cost is read from how late the next 16 ms timer tick came. The tick's own work is already
    /// inside that gap, because Avalonia restarts the timer only after the handler returns, so it is
    /// counted once, as a floor. OxyPlot lays out and builds its canvas shapes after the tick returns.
    /// </summary>
    [Theory]
    [InlineData(16, 0.5, 0.5)]    // the next tick came on time: only the tick's own work
    [InlineData(10, 0.5, 0.5)]    // early (timer jitter) never reads as less than the tick's work
    [InlineData(41, 25.0, 25.0)]  // a 25 ms handler, then the 16 ms wait: 25 ms of work, not 50
    [InlineData(66, 1.0, 50.0)]   // a 1 ms tick, then 49 ms of layout and drawing before the next
    public void The_redraw_cost_is_how_late_the_next_tick_came(long gapMs, double tickWorkMs, double expected) =>
        Assert.Equal(expected, PlotLogger.RedrawCostMs(gapMs, tickWorkMs), 3);

    [Fact]
    public void A_redraw_that_cost_50_ms_is_not_due_again_for_150_ms()
    {
        Assert.False(PlotLogger.IsRedrawDue(149, 0, 50));
        Assert.True(PlotLogger.IsRedrawDue(150, 0, 50));
    }

    [Theory]
    [InlineData(1000, 0, false)]
    [InlineData(1001, 0, true)]
    [InlineData(500, 0, false)]
    public void Stats_stay_on_a_one_second_cadence(long now, long last, bool expected) =>
        Assert.Equal(expected, PlotLogger.IsStatsDue(now, last));
}

/// <summary>
/// On the app-host collection for the same reason as <c>ChannelColorLivePlotTests</c>:
/// <c>AddChannelSeries</c> reads <c>LoggingManager.Instance</c>, whose constructor needs
/// <c>App.InitializeMobile()</c>.
/// </summary>
[Collection(AppHostCollection.Name)]
public class LivePlotDecimationTests
{
    public LivePlotDecimationTests() => Daqifi.Desktop.App.InitializeMobile();

    [Fact]
    public void Each_live_series_decimates_to_min_max_per_pixel()
    {
        var plotter = new PlotLogger();

        plotter.Log(Sample("AI0"));
        plotter.DrainPendingSamples();
        plotter.Log(Sample("AI1"));
        plotter.DrainPendingSamples();

        Assert.Equal(2, plotter.LoggedChannels.Count);
        Assert.All(plotter.LoggedChannels.Values,
            series => Assert.Equal(OxyPlot.Decimator.Decimate, series.Decimator));
    }

    [Fact]
    public void Decimation_does_not_drop_buffered_points()
    {
        // Decimation happens at draw time in screen space. The buffer, the tracker and the
        // plot-stats summary must still see every sample.
        var plotter = new PlotLogger();
        for (var i = 0; i < 100; i++)
        {
            plotter.Log(Sample("AI0", tickOffset: i * 10_000));
            plotter.DrainPendingSamples();
        }

        Assert.Equal(100, plotter.LoggedPoints.Values.Single().Count);
    }

    private static DataSample Sample(string channel, long tickOffset = 0) => new()
    {
        ChannelName = channel,
        DeviceName = "Nyquist",
        DeviceSerialNo = "SN-1",
        Type = ChannelType.Analog,
        Color = "#FFD32F2F",
        Value = 1.25,
        TimestampTicks = 638000000000000000 + tickOffset
    };
}
