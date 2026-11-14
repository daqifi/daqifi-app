using System.Net;
using System.Text.RegularExpressions;
using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Helpers;
using Daqifi.Desktop.Logger;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Issue #585, end to end: the time-axis labels both plots actually DRAW, read back from an SVG render
/// of the real models. <see cref="ElapsedTimeFormatTests"/> pins the arithmetic; these pin that the
/// Logged Data plot (<see cref="PlotModelFactory"/>) and the Live Graph (<see cref="PlotLogger"/>) use
/// it, at the durations the issue names — a ten-hour session, a stream past 17 minutes, and a zoom of a
/// few seconds into a long session.
/// </summary>
/// <remarks>On the app-host collection because <see cref="PlotLogger"/>'s <c>AddChannelSeries</c>
/// reads <c>LoggingManager.Instance</c>, as in <c>LivePlotWindowTests</c>.</remarks>
[Collection(AppHostCollection.Name)]
public partial class TimeAxisLabelTests
{
    private const double Hour = ElapsedTimeFormat.MS_PER_HOUR;
    private const double Minute = ElapsedTimeFormat.MS_PER_MINUTE;
    private const long T0 = 638_000_000_000_000_000;

    public TimeAxisLabelTests() => Daqifi.Desktop.App.InitializeMobile();

    [Fact]
    public void A_ten_hour_logged_session_reads_0_00_00_to_10_00_00_on_whole_hours()
    {
        var model = new PlotModelFactory().CreateMainPlotModel();
        model.Series.Add(SessionSeries(spanMs: 10 * Hour, everyMs: Minute));

        var labels = RenderedTimeLabels(model, out var axis);

        AssertNoScientificNotation(labels);
        Assert.Equal("Elapsed time (h:mm:ss)", axis.ActualTitle);
        Assert.Equal(Hour, axis.ActualMajorStep);
        Assert.Contains("0:00:00", labels);
        Assert.Contains("10:00:00", labels);
    }

    /// <summary>The issue's verification step 4: zoomed to seconds inside a long session, the labels stay distinct.</summary>
    [Fact]
    public void A_few_seconds_zoomed_into_a_long_session_keep_distinct_labels()
    {
        var model = new PlotModelFactory().CreateMainPlotModel();
        model.Series.Add(SessionSeries(spanMs: 10 * Hour, everyMs: 100));
        var axis = model.Axes.Single(a => a.Key == PlotModelFactory.TIME_AXIS_KEY);
        axis.Zoom(5 * Hour, 5 * Hour + 3_000);

        var labels = RenderedTimeLabels(model, out _);

        AssertNoScientificNotation(labels);
        Assert.True(labels.Count >= 5, $"only {labels.Count} label(s): {string.Join(", ", labels)}");
        Assert.Equal(labels.Count, labels.Distinct().Count());
        Assert.All(labels, l => Assert.StartsWith("5:00:0", l));
    }

    /// <summary>The empty frame shows a minute, not 0 to 100 ms (which in this format would be a tenth of a second).</summary>
    [Fact]
    public void The_empty_logged_plot_frame_reads_one_minute()
    {
        var labels = RenderedTimeLabels(new PlotModelFactory().CreateMainPlotModel(), out var axis);

        Assert.Equal("Elapsed time (m:ss)", axis.ActualTitle);
        Assert.Equal("0:00", labels.First());
        Assert.Equal("1:00", labels.Last());
    }

    /// <summary>The issue's verification step 3, with FirstTime 20 minutes behind the newest sample.</summary>
    [Fact]
    public void A_live_stream_past_17_minutes_reads_minutes_and_seconds()
    {
        var plotter = new PlotLogger();
        for (var second = 0; second <= 20 * 60; second++)
        {
            plotter.Log(LiveSample(second * 10_000_000L));
        }

        var labels = RenderedTimeLabels(plotter.PlotModel, out var axis);

        AssertNoScientificNotation(labels);
        Assert.Equal("Elapsed time (m:ss)", axis.ActualTitle);
        Assert.Contains("20:00", labels);
    }

    [Fact]
    public void A_live_stream_of_ten_hours_reads_hours()
    {
        var plotter = new PlotLogger();
        for (var second = 0; second <= 10 * 3600; second += 10)
        {
            plotter.Log(LiveSample(second * 10_000_000L));
        }

        var labels = RenderedTimeLabels(plotter.PlotModel, out var axis);

        AssertNoScientificNotation(labels);
        Assert.Equal("Elapsed time (h:mm:ss)", axis.ActualTitle);
        Assert.Contains("10:00:00", labels);
    }

    [Fact]
    public void Both_plots_build_their_time_axis_from_the_one_shared_type()
    {
        var logged = new PlotModelFactory().CreateMainPlotModel().Axes.Single(a => a.Position == AxisPosition.Bottom);
        var live = new PlotLogger().PlotModel.Axes.Single(a => a.Position == AxisPosition.Bottom);

        Assert.IsType<ElapsedTimeAxis>(logged);
        Assert.IsType<ElapsedTimeAxis>(live);
    }

    /// <summary>
    /// Renders <paramref name="model"/> to SVG at a desktop plot size and returns the labels drawn on
    /// the time axis: every SVG text that the axis's own formatter produces for one of its ticks.
    /// </summary>
    private static List<string> RenderedTimeLabels(PlotModel model, out Axis axis)
    {
        var svg = SvgExporter.ExportToString(model, 1200, 500, isDocument: false);
        var time = model.Axes.Single(a => a.Position == AxisPosition.Bottom);
        axis = time;

        time.GetTickValues(out var majorLabelValues, out _, out _);
        var expected = majorLabelValues
            .Where(v => v >= time.ActualMinimum && v <= time.ActualMaximum)
            .Select(time.FormatValue)
            .ToList();
        var drawn = SvgText().Matches(svg).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToHashSet();

        Assert.NotEmpty(expected);
        Assert.All(expected, label => Assert.Contains(label, drawn));
        Assert.Contains(time.ActualTitle, drawn);
        return expected;
    }

    private static void AssertNoScientificNotation(IEnumerable<string> labels) =>
        Assert.All(labels, l => Assert.DoesNotMatch("[eE][+-]?[0-9]", l));

    private static LineSeries SessionSeries(double spanMs, double everyMs)
    {
        var series = new LineSeries
        {
            XAxisKey = PlotModelFactory.TIME_AXIS_KEY,
            YAxisKey = PlotModelFactory.ANALOG_AXIS_KEY
        };
        for (var x = 0.0; x <= spanMs; x += everyMs)
        {
            series.Points.Add(new DataPoint(x, Math.Sin(x / Hour)));
        }

        return series;
    }

    private static DataSample LiveSample(long ticksSinceStart) => new()
    {
        ChannelName = "AI0",
        DeviceName = "Nyquist",
        DeviceSerialNo = "SN-1",
        Type = ChannelType.Analog,
        Color = "#FFD32F2F",
        Value = 1.25,
        TimestampTicks = T0 + ticksSinceStart
    };

    [GeneratedRegex("<text[^>]*>([^<]*)</text>")]
    private static partial Regex SvgText();
}
