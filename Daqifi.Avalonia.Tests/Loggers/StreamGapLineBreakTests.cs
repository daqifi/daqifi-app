using Daqifi.Avalonia.Tests.Device;
using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Issue #629: the live plot breaks its line where frames were lost, and it takes that gap from
/// Core's <c>DaqifiStreamingDevice.GapDetected</c>. The app used to run its own copy of Core's
/// detector in <c>PlotLogger</c>, once per sample of each channel.
/// </summary>
/// <remarks>
/// On the app-host collection because the drain creates series, and <c>AddChannelSeries</c> reads
/// <c>LoggingManager.Instance</c>.
/// </remarks>
[Collection(AppHostCollection.Name)]
public sealed class StreamGapLineBreakTests : IDisposable
{
    private readonly CoreDecodingHarness _harness;

    public StreamGapLineBreakTests()
    {
        Daqifi.Desktop.App.InitializeMobile();
        _harness = CoreDecodingHarness.Create(analogPorts: 3);
    }

    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// The whole path: a real Core device decodes the frames and finds the gap, the app publishes
    /// the samples, and the plot draws them. Each series gets exactly one break, in front of its
    /// first point after the gap.
    /// </summary>
    [Fact]
    public void A_gap_in_a_real_Core_stream_breaks_each_series_once_in_front_of_the_next_sample()
    {
        var plotter = PlotterFor(_harness.EnableAnalog(0), _harness.EnableAnalog(1));
        _harness.Device.InitializeStreaming();

        // Two frames lost between the fourth frame and the fifth.
        Feed(0, 1, 2, 3, 6, 7, 8);
        plotter.DrainPendingSamples();

        Assert.Equal(2, plotter.LoggedPoints.Count);
        Assert.All(plotter.LoggedPoints.Values, points =>
        {
            Assert.Equal(8, points.Count);
            Assert.Equal(new[] { 4 }, Enumerable.Range(0, points.Count).Where(i => double.IsNaN(points[i].X)));
            // The break is between the last point before the gap and the first point after it.
            Assert.Equal(30.0, points[5].X - points[3].X, precision: 3);
        });
    }

    [Fact]
    public void A_clean_real_Core_stream_draws_unbroken_lines()
    {
        var plotter = PlotterFor(_harness.EnableAnalog(0), _harness.EnableAnalog(1));
        _harness.Device.InitializeStreaming();

        Feed(Enumerable.Range(0, 100).Select(i => (uint)i).ToArray());
        plotter.DrainPendingSamples();

        Assert.All(plotter.LoggedPoints.Values, points =>
        {
            Assert.Equal(100, points.Count);
            Assert.DoesNotContain(points, point => double.IsNaN(point.X));
        });
    }

    /// <summary>
    /// The plot breaks a line only in front of a sample that carries the mark, and only that
    /// sample's own line: a gap mark on one channel's sample says nothing about another channel.
    /// </summary>
    [Fact]
    public void Only_a_sample_that_follows_a_gap_breaks_the_line_and_only_its_own()
    {
        var plotter = new PlotLogger();

        plotter.Log(Sample("AI0", 0));
        plotter.Log(Sample("AI1", 0));
        plotter.Log(Sample("AI0", 5, followsGap: true));
        plotter.Log(Sample("AI1", 5));
        plotter.Log(Sample("AI0", 6));
        plotter.Log(Sample("AI1", 6));
        plotter.DrainPendingSamples();

        var ai0 = plotter.LoggedPoints[("SN-1", "AI0")];
        var ai1 = plotter.LoggedPoints[("SN-1", "AI1")];
        Assert.Equal(4, ai0.Count);
        Assert.True(double.IsNaN(ai0[1].X), "the sample after the gap must break its line");
        Assert.Equal(new[] { 0.0, 5.0, 6.0 }, ai0.Where(p => !double.IsNaN(p.X)).Select(p => p.X));
        Assert.Equal(new[] { 0.0, 5.0, 6.0 }, ai1.Select(p => p.X));
    }

    private static PlotLogger PlotterFor(params AnalogChannel[] channels)
    {
        var plotter = new PlotLogger();
        foreach (var channel in channels)
        {
            channel.OnChannelUpdated += (_, sample) => plotter.Log(sample);
        }

        return plotter;
    }

    /// <summary>Feeds one two-channel frame at each of these whole sample periods from the start.</summary>
    private void Feed(params uint[] periodsFromStart)
    {
        foreach (var periods in periodsFromStart)
        {
            _harness.Core.Feed(CoreDecodingHarness.FloatFrame(
                CoreDecodingHarness.FirstCounter + periods * _harness.TicksPerSample, 1f, 2f));
        }
    }

    /// <summary>One sample per millisecond index, so a point's X (elapsed ms) equals its index.</summary>
    private static DataSample Sample(string channel, int index, bool followsGap = false) => new()
    {
        ChannelName = channel,
        DeviceName = "Nyquist",
        DeviceSerialNo = "SN-1",
        Type = ChannelType.Analog,
        Color = "#FFD32F2F",
        Value = 1.25,
        TimestampTicks = 638000000000000000 + index * 10_000L,
        FollowsGap = followsGap
    };
}
