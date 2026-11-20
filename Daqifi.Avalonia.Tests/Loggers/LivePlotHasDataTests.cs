using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins <see cref="PlotLogger.HasPlotData"/>, the flag the Live Graph pane shows its plot by (#616).
/// </summary>
/// <remarks>
/// <para>The pane used to show the plot while an input channel was active. Review on #658 measured
/// two wrong states: a channel on with zero points drew OxyPlot's default 0 to 100 frame, and a
/// disconnect hid a 120-point trace. The flag follows the points instead: true from the first
/// drained sample, false after <see cref="PlotLogger.ClearPlot"/>, and raised only on a change.</para>
/// <para>These tests drain through <c>DrainPendingSamples</c>. The render tick, the production
/// drain, raises through the same <c>RaiseAfterDrain</c>; HeadlessBench <c>CONN-LOST</c> checks
/// that path in the running pane.</para>
/// <para>On the app-host collection because the drain creates series, and
/// <c>AddChannelSeries</c> reads <c>LoggingManager.Instance</c>.</para>
/// </remarks>
[Collection(AppHostCollection.Name)]
public class LivePlotHasDataTests
{
    public LivePlotHasDataTests() => Daqifi.Desktop.App.InitializeMobile();

    [Fact]
    public void A_new_plot_has_no_data()
    {
        Assert.False(new PlotLogger().HasPlotData);
    }

    [Fact]
    public void A_queued_sample_is_not_data_until_it_is_drained()
    {
        var plotter = new PlotLogger();

        plotter.Log(Sample(0));

        Assert.False(plotter.HasPlotData);
    }

    [Fact]
    public void A_drain_with_nothing_queued_leaves_the_plot_without_data()
    {
        var plotter = new PlotLogger();
        var raised = CountHasPlotDataChanges(plotter);

        Assert.Equal(0, plotter.DrainPendingSamples());

        Assert.False(plotter.HasPlotData);
        Assert.Equal(0, raised());
    }

    [Fact]
    public void The_first_drained_sample_sets_the_flag_and_notifies_once()
    {
        var plotter = new PlotLogger();
        var raised = CountHasPlotDataChanges(plotter);

        plotter.Log(Sample(0));
        plotter.Log(Sample(1));
        plotter.DrainPendingSamples();

        Assert.True(plotter.HasPlotData);
        Assert.Equal(1, raised());

        // More points change nothing the pane binds to: no second notification.
        plotter.Log(Sample(2));
        plotter.DrainPendingSamples();

        Assert.True(plotter.HasPlotData);
        Assert.Equal(1, raised());
    }

    [Fact]
    public void Clearing_the_plot_clears_the_flag_and_notifies()
    {
        var plotter = new PlotLogger();
        plotter.Log(Sample(0));
        plotter.DrainPendingSamples();
        var raised = CountHasPlotDataChanges(plotter);

        plotter.ClearPlot();

        Assert.False(plotter.HasPlotData);
        Assert.Equal(1, raised());

        // The next acquisition's first point sets it again.
        plotter.Log(Sample(1));
        plotter.DrainPendingSamples();

        Assert.True(plotter.HasPlotData);
        Assert.Equal(2, raised());
    }

    private static Func<int> CountHasPlotDataChanges(PlotLogger plotter)
    {
        var count = 0;
        plotter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlotLogger.HasPlotData))
            {
                count++;
            }
        };
        return () => count;
    }

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
