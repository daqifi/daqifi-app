using Daqifi.Desktop.Logger;
using OxyPlot;
using OxyPlot.Axes;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins the frame <see cref="PlotModelFactory.CreateMainPlotModel"/> builds for the Logged Data plot:
/// no series at construction, and three titled axes.
///
/// <para>History. Issue #251 found the plot region a blank rectangle with no copy when no session was
/// selected, and made the <c>PlotView</c> always visible, so the empty region drew this frame. Issue
/// #616 reversed that: an empty model has no data range, OxyPlot then draws its default 0 to 100, and
/// the pane showed a "0 to 100 V" grid that no device measures. The view is now hidden until
/// <c>DatabaseLogger.HasSessionData</c> is true, and the app-wide empty state fills the region. That
/// swap is XAML, so the parity-audit capture verifies it, not this file.</para>
///
/// <para>What these tests still guard is the frame the plot draws once a session HAS data:
/// <see cref="DatabaseLogger"/>'s constructor assigns <c>PlotModel = CreateMainPlotModel()</c> before
/// any session loads, and <c>DisplayLoggingSession</c> adds series to those keyed axes.</para>
/// </summary>
public class EmptyLoggedPlotFrameTests
{
    private static PlotModel AFreshMainModel() => new PlotModelFactory().CreateMainPlotModel();

    /// <summary>
    /// The frame is empty before a session is chosen — the plot cannot show another session's traces
    /// now that it is drawn rather than collapsed. This is the half that makes always-visible safe.
    /// </summary>
    [Fact]
    public void A_freshly_built_main_model_carries_no_series()
    {
        Assert.Empty(AFreshMainModel().Series);
    }

    /// <summary>
    /// The frame is labelled — these are the three titles the issue names as what Live Graph gets
    /// right and Logged Data did not, and they are what turns an empty region into a legible "nothing
    /// plotted yet" rather than "the plot failed to render".
    /// </summary>
    [Theory]
    [InlineData(PlotModelFactory.ANALOG_AXIS_KEY, "Analog (V)", AxisPosition.Left)]
    [InlineData(PlotModelFactory.DIGITAL_AXIS_KEY, "Digital", AxisPosition.Right)]
    [InlineData(PlotModelFactory.TIME_AXIS_KEY, "Elapsed time", AxisPosition.Bottom)]
    public void A_freshly_built_main_model_carries_the_three_titled_axes(
        string key, string expectedTitle, AxisPosition expectedPosition)
    {
        var axis = Assert.Single(AFreshMainModel().Axes, a => a.Key == key);

        Assert.Equal(expectedTitle, axis.Title);
        Assert.Equal(expectedPosition, axis.Position);
    }

    /// <summary>
    /// Titles alone would leave the interior blank. The analog and time axes carry gridlines, which is
    /// what fills the region and makes it read as a plot; the digital axis deliberately draws none, so
    /// asserting "every axis has gridlines" would be wrong and asserting nothing would miss the point.
    /// </summary>
    [Fact]
    public void The_analog_and_time_axes_draw_the_gridlines_that_fill_the_empty_frame()
    {
        var axes = AFreshMainModel().Axes;

        foreach (var key in new[] { PlotModelFactory.ANALOG_AXIS_KEY, PlotModelFactory.TIME_AXIS_KEY })
        {
            var axis = Assert.Single(axes, a => a.Key == key);
            Assert.Equal(LineStyle.Solid, axis.MajorGridlineStyle);
            Assert.Equal(LineStyle.Solid, axis.MinorGridlineStyle);
        }

        var digital = Assert.Single(axes, a => a.Key == PlotModelFactory.DIGITAL_AXIS_KEY);
        Assert.Equal(LineStyle.None, digital.MajorGridlineStyle);
    }
}
