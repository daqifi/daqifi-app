using OxyPlot;
using OxyPlot.Axes;

namespace Daqifi.Desktop.Helpers;

/// <summary>
/// The horizontal time axis of the Live Graph and the Logged Data plot. X is milliseconds since the
/// first sample, as before; the labels read as a clock (<c>0:00</c> … <c>16:40</c>, or
/// <c>0:00:00</c> … <c>10:00:00</c> once the range reaches an hour) on clock-friendly tick spacings,
/// and the title names the format in use: <c>Elapsed time (m:ss)</c> or <c>Elapsed time (h:mm:ss)</c>.
/// </summary>
/// <remarks>
/// <para>Issue #585: a plain <c>LinearAxis</c> labelled these values <c>2e+06 … 3.6e+07</c> for any
/// session or stream longer than about 17 minutes. The arithmetic lives in
/// <see cref="ElapsedTimeFormat"/>; this class only wires it into OxyPlot's layout pass.</para>
/// <para>Both <c>PlotModelFactory</c> and <c>PlotLogger</c> build their time axis from this one type,
/// so the two plots cannot disagree on how time reads.</para>
/// </remarks>
public sealed class ElapsedTimeAxis : LinearAxis
{
    public const string TITLE = "Elapsed time";
    public const string MINUTES_UNIT = "m:ss";
    public const string HOURS_UNIT = "h:mm:ss";

    /// <summary>
    /// The range drawn before there is any data: one minute, so an empty frame reads
    /// <c>0:00 … 1:00</c> rather than OxyPlot's default 0 to 100, which here would be a tenth of a
    /// second.
    /// </summary>
    public const double EMPTY_RANGE_MS = ElapsedTimeFormat.MS_PER_MINUTE;

    /// <summary>
    /// The narrowest window a zoom can reach. At this width the axis still draws ticks 1 or 2 ms
    /// apart, so the labels (to the millisecond) always differ from each other.
    /// </summary>
    public const double MINIMUM_RANGE_MS = 10;

    public ElapsedTimeAxis()
    {
        Position = AxisPosition.Bottom;
        TickStyle = TickStyle.None;
        MajorGridlineStyle = LineStyle.Solid;
        MinorGridlineStyle = LineStyle.Solid;
        TitleFontSize = 12;
        TitleFontWeight = FontWeights.Bold;
        Title = TITLE;
        TitleFormatString = "{0} ({1})";
        Unit = MINUTES_UNIT;
        // The default 4 px left the title touching the label above it.
        AxisTitleDistance = 8;
        MinimumRange = MINIMUM_RANGE_MS;
        // A clock label (10:00:00) is wider than the number it replaces; 80 px per interval keeps a
        // gap between neighbours at the plot's narrowest (the 720 px minimum window).
        IntervalLength = 80;
    }

    /// <inheritdoc />
    /// <remarks>Also settles the title's unit for this layout pass, since both depend on the same
    /// visible range and OxyPlot calls this once per pass, before any label is drawn.</remarks>
    protected override double CalculateActualInterval(
        double availableSize, double maxIntervalSize, double minIntervalCount, double maxIntervalCount)
    {
        Unit = ElapsedTimeFormat.NeedsHours(ActualMinimum, ActualMaximum) ? HOURS_UNIT : MINUTES_UNIT;
        return ElapsedTimeFormat.ChooseMajorStep(
            ActualMaximum - ActualMinimum, availableSize, maxIntervalSize, maxIntervalCount);
    }

    /// <inheritdoc />
    protected override double CalculateMinorInterval(double majorInterval) =>
        ElapsedTimeFormat.MinorStepFor(majorInterval);

    /// <inheritdoc />
    protected override string FormatValueOverride(double x) =>
        ElapsedTimeFormat.Format(x, ActualMajorStep, Unit == HOURS_UNIT, ActualCulture);

    /// <summary>
    /// The value the hover tracker shows for <paramref name="x"/>: the instant to the millisecond,
    /// already written as a clock reading (so a tracker format uses it as <c>{2}</c>, with no
    /// numeric format).
    /// </summary>
    public override object GetValue(double x) => ElapsedTimeFormat.FormatPrecise(x, ActualCulture);

    /// <inheritdoc />
    protected override double CalculateActualMaximum() =>
        double.IsNaN(DataMaximum) ? EMPTY_RANGE_MS : base.CalculateActualMaximum();

    /// <inheritdoc />
    protected override double CalculateActualMinimum() =>
        double.IsNaN(DataMinimum) ? 0 : base.CalculateActualMinimum();
}
