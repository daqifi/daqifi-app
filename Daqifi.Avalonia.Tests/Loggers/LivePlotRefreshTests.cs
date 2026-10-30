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
    [InlineData(49, 0, false)]
    [InlineData(50, 0, true)]
    [InlineData(100, 0, true)]   // false under the old 1 s throttle
    [InlineData(1050, 1000, true)]
    public void Redraw_is_due_every_50_ms(long now, long last, bool expected) =>
        Assert.Equal(expected, PlotLogger.IsRedrawDue(now, last));

    [Fact]
    public void Redraw_runs_at_least_ten_times_a_second() =>
        Assert.True(PlotLogger.REDRAW_INTERVAL_MS <= 100,
            $"REDRAW_INTERVAL_MS is {PlotLogger.REDRAW_INTERVAL_MS} ms; the live plot should redraw at least 10 times a second.");

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
        plotter.Log(Sample("AI1"));

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
