using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins that the device transport thread never waits for the live plot (issue #595).
/// </summary>
/// <remarks>
/// <para><c>PlotLogger.Log</c> runs on the device transport thread, once per sample. It used to take
/// <c>PlotModel.SyncRoot</c>, the lock the UI thread holds while OxyPlot updates and draws. One long
/// UI job then stopped the USB reader, the board's buffer overflowed, and a 16-channel session lost
/// 44 s of samples per channel with no message. <c>Log</c> now only enqueues, and the render tick
/// moves the queued samples into the series on the UI thread.</para>
/// <para>On the app-host collection because the drain creates series, and
/// <c>AddChannelSeries</c> reads <c>LoggingManager.Instance</c>.</para>
/// </remarks>
[Collection(AppHostCollection.Name)]
public class LivePlotIngestDecouplingTests
{
    public LivePlotIngestDecouplingTests() => Daqifi.Desktop.App.InitializeMobile();

    [Fact]
    public void Log_returns_while_another_thread_holds_the_plot_lock()
    {
        var plotter = new PlotLogger();
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // The UI thread in the bug: it holds the plot lock for a long redraw.
        var holder = new Thread(() =>
        {
            lock (plotter.PlotModel.SyncRoot)
            {
                holding.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        }) { IsBackground = true };
        holder.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            // The transport thread: 16 channels, 100 frames. Before #595 the first call blocked until
            // the lock was released, so this task could not finish while it was held.
            var ingest = new Thread(() =>
            {
                for (var frame = 0; frame < 100; frame++)
                {
                    for (var channel = 0; channel < 16; channel++)
                    {
                        plotter.Log(Sample($"AI{channel}", frame));
                    }
                }
            }) { IsBackground = true };
            ingest.Start();

            Assert.True(ingest.Join(TimeSpan.FromSeconds(2)),
                "PlotLogger.Log waited for the plot lock: the device reader would stop while the UI draws.");
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));
        }

        // Nothing logged while the lock was held is lost: the drain plots all of it, in order.
        Assert.Equal(1600, plotter.DrainPendingSamples());
        Assert.Equal(16, plotter.LoggedChannels.Count);
        Assert.All(plotter.LoggedPoints.Values, points =>
        {
            Assert.Equal(100, points.Count);
            Assert.Equal(Enumerable.Range(0, 100).Select(i => (double)i), points.Select(p => p.X));
        });
    }

    [Fact]
    public void Logged_samples_reach_the_series_only_when_drained()
    {
        var plotter = new PlotLogger();

        plotter.Log(Sample("AI0", 0));
        plotter.Log(Sample("AI0", 1));

        // Log does not touch the series: that is UI-thread work.
        Assert.Empty(plotter.LoggedChannels);

        Assert.Equal(2, plotter.DrainPendingSamples());
        Assert.Equal(2, plotter.LoggedPoints.Values.Single().Count);
        Assert.Equal(0, plotter.DrainPendingSamples());
    }

    [Fact]
    public void Clearing_the_plot_discards_samples_still_queued()
    {
        var plotter = new PlotLogger();
        for (var i = 0; i < 5; i++)
        {
            plotter.Log(Sample("AI0", i));
        }

        plotter.ClearPlot();

        Assert.Equal(0, plotter.DrainPendingSamples());
        Assert.Empty(plotter.LoggedChannels);
    }

    [Fact]
    public void A_ui_thread_that_never_drains_cannot_grow_the_queue_without_bound()
    {
        var plotter = new PlotLogger();
        var total = PlotLogger.MAX_PENDING_SAMPLES + 1000;
        for (var i = 0; i < total; i++)
        {
            plotter.Log(Sample("AI0", i));
        }

        // The oldest 1000 were dropped from the plot and the newest are all kept. X is elapsed time
        // from the first plotted sample, so the newest one, sample total - 1, reads total - 1001.
        Assert.Equal(PlotLogger.MAX_PENDING_SAMPLES, plotter.DrainPendingSamples());
        Assert.Equal(new DateTime(Sample("AI0", 1000).TimestampTicks), plotter.FirstTime);
        Assert.Equal(total - 1001, plotter.LoggedPoints.Values.Single()[^1].X);
    }

    [Fact]
    public void Samples_dropped_between_drains_leave_a_break_in_the_line_not_a_join()
    {
        var plotter = new PlotLogger(maxPendingSamples: 10);
        plotter.Log(Sample("AI0", 0));
        plotter.DrainPendingSamples();

        // 15 more with room for 10: samples 1 to 5 are dropped from the plot.
        for (var i = 1; i <= 15; i++)
        {
            plotter.Log(Sample("AI0", i));
        }

        Assert.Equal(10, plotter.DrainPendingSamples());
        var points = plotter.LoggedPoints.Values.Single();
        Assert.Equal(12, points.Count);
        Assert.Equal(0, points[0].X);
        Assert.True(double.IsNaN(points[1].X), "the hole must break the line");
        Assert.Equal(Enumerable.Range(6, 10).Select(i => (double)i), points.Skip(2).Select(p => p.X));

        // The break is drawn once: the next drain, with nothing dropped, adds no marker.
        plotter.Log(Sample("AI0", 16));
        plotter.DrainPendingSamples();
        Assert.Equal(13, points.Count);
        Assert.Equal(16, points[^1].X);
    }

    [Fact]
    public void Ingest_and_drain_on_two_threads_lose_and_reorder_nothing()
    {
        var plotter = new PlotLogger();
        const int frames = 3000;
        var producer = new Thread(() =>
        {
            for (var frame = 0; frame < frames; frame++)
            {
                plotter.Log(Sample("AI0", frame));
                plotter.Log(Sample("AI1", frame));
            }
        }) { IsBackground = true };
        producer.Start();

        // The UI thread: drains on its own cadence, and holds the lock now and then as a redraw does.
        while (producer.IsAlive)
        {
            plotter.DrainPendingSamples();
            lock (plotter.PlotModel.SyncRoot)
            {
                Thread.SpinWait(2000);
            }
        }

        Assert.True(producer.Join(TimeSpan.FromSeconds(10)));
        plotter.DrainPendingSamples();

        Assert.All(plotter.LoggedPoints.Values, points =>
        {
            Assert.Equal(frames, points.Count);
            Assert.Equal(Enumerable.Range(0, frames).Select(i => (double)i), points.Select(p => p.X));
        });
    }

    /// <summary>One sample per millisecond, so a point's X (elapsed ms) equals its index.</summary>
    private static DataSample Sample(string channel, int index) => new()
    {
        ChannelName = channel,
        DeviceName = "Nyquist",
        DeviceSerialNo = "SN-1",
        Type = ChannelType.Analog,
        Color = "#FFD32F2F",
        Value = 1.25,
        TimestampTicks = 638000000000000000 + index * 10_000L
    };
}
