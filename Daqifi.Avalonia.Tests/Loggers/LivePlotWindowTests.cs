using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins how many points each live-plot series keeps, and how the oldest ones leave (issue #495).
/// </summary>
/// <remarks>
/// <para>The buffer used to drop its oldest point with <c>List.RemoveAt(0)</c> on every sample once
/// full. That moves the whole buffer down one slot, about 80 KB per sample, on the transport thread
/// and while holding the lock the redraw needs. The buffer now trims in batches, so it holds between
/// 5,000 and 5,249 points once full and the oldest point moves once per batch.</para>
/// <para>The numbers are literal on purpose: they are what the user sees (the width of the live
/// window), so a change to them must change this file too.</para>
/// <para>On the app-host collection for the same reason as <c>LivePlotDecimationTests</c>:
/// <c>AddChannelSeries</c> reads <c>LoggingManager.Instance</c>.</para>
/// </remarks>
[Collection(AppHostCollection.Name)]
public class LivePlotWindowTests
{
    private const int Window = 5000;
    private const int Batch = 250;

    public LivePlotWindowTests() => Daqifi.Desktop.App.InitializeMobile();

    [Fact]
    public void A_full_series_holds_at_least_the_window_and_less_than_one_batch_more()
    {
        var plotter = new PlotLogger();
        var sample = 0;
        for (; sample < Window; sample++)
        {
            plotter.Log(Sample(sample));
        }

        var points = plotter.LoggedPoints.Values.Single();
        for (; sample < Window + 3 * Batch + 17; sample++)
        {
            plotter.Log(Sample(sample));
            Assert.InRange(points.Count, Window, Window + Batch - 1);
        }

        // The newest sample is always kept: trimming takes from the old end only.
        Assert.Equal(sample - 1, points[^1].X);
    }

    [Fact]
    public void The_oldest_point_moves_once_per_batch_not_once_per_sample()
    {
        var plotter = new PlotLogger();
        var sample = 0;
        for (; sample < Window; sample++)
        {
            plotter.Log(Sample(sample));
        }

        var points = plotter.LoggedPoints.Values.Single();
        var oldest = points[0].X;
        var moves = 0;
        const int extra = 4 * Batch;
        for (var i = 0; i < extra; i++, sample++)
        {
            plotter.Log(Sample(sample));
            if (points[0].X != oldest)
            {
                moves++;
                oldest = points[0].X;
            }
        }

        Assert.InRange(moves, 1, extra / Batch);
    }

    /// <summary>One sample per millisecond, so a point's X (elapsed ms) equals its index.</summary>
    private static DataSample Sample(int index) => new()
    {
        ChannelName = "AI0",
        DeviceName = "Nyquist",
        DeviceSerialNo = "SN-1",
        Type = ChannelType.Analog,
        Color = "#FFD32F2F",
        Value = 1.25,
        TimestampTicks = 638000000000000000 + index * 10_000L
    };
}
