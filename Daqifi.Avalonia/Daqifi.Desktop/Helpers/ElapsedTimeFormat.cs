using System.Globalization;
using System.Text;

namespace Daqifi.Desktop.Helpers;

/// <summary>
/// The arithmetic behind <see cref="ElapsedTimeAxis"/>: which tick spacing to use for a visible range
/// of elapsed milliseconds, and how to write one tick as a clock reading (<c>m:ss</c> or
/// <c>h:mm:ss</c>, with a fraction only when the spacing needs one).
/// </summary>
/// <remarks>
/// <para>Issue #585. Both plots put "milliseconds since the first sample" on X. A plain
/// <c>LinearAxis</c> with no format writes those with the general format, which switches to scientific
/// notation at 1,000,000 ms (about 17 minutes) — a ten-hour session read <c>0, 2e+06 … 3.6e+07</c>.
/// Its decimal 1-2-5 tick spacing is also wrong for time: a 5,000,000 ms step lands on 1:23:20.</para>
/// <para>X stays in milliseconds. Only the labels and the tick spacing change, so zoom, the minimap,
/// the query-on-zoom range and every ticks-to-X conversion keep working as they are.</para>
/// <para>Kept apart from the axis so it can be tested without an OxyPlot render.</para>
/// </remarks>
public static class ElapsedTimeFormat
{
    public const double MS_PER_SECOND = 1_000;
    public const double MS_PER_MINUTE = 60 * MS_PER_SECOND;
    public const double MS_PER_HOUR = 60 * MS_PER_MINUTE;
    public const double MS_PER_DAY = 24 * MS_PER_HOUR;

    /// <summary>
    /// Every major tick spacing the axis may use, smallest first, each with the minor spacing drawn
    /// between its ticks. The steps are the ones a clock divides into evenly (15 s, 30 min, 3 h …),
    /// never a decimal step such as 5,000,000 ms that would put ticks on odd times.
    /// </summary>
    private static readonly (double Major, double Minor)[] Steps =
    [
        (1, 0.2), (2, 0.5), (5, 1),
        (10, 2), (20, 5), (50, 10),
        (100, 20), (200, 50), (500, 100),
        (1 * MS_PER_SECOND, 200), (2 * MS_PER_SECOND, 500), (5 * MS_PER_SECOND, 1 * MS_PER_SECOND),
        (10 * MS_PER_SECOND, 2 * MS_PER_SECOND), (15 * MS_PER_SECOND, 5 * MS_PER_SECOND),
        (30 * MS_PER_SECOND, 10 * MS_PER_SECOND),
        (1 * MS_PER_MINUTE, 15 * MS_PER_SECOND), (2 * MS_PER_MINUTE, 30 * MS_PER_SECOND),
        (5 * MS_PER_MINUTE, 1 * MS_PER_MINUTE), (10 * MS_PER_MINUTE, 2 * MS_PER_MINUTE),
        (15 * MS_PER_MINUTE, 5 * MS_PER_MINUTE), (30 * MS_PER_MINUTE, 10 * MS_PER_MINUTE),
        (1 * MS_PER_HOUR, 15 * MS_PER_MINUTE), (2 * MS_PER_HOUR, 30 * MS_PER_MINUTE),
        (3 * MS_PER_HOUR, 1 * MS_PER_HOUR), (6 * MS_PER_HOUR, 2 * MS_PER_HOUR),
        (12 * MS_PER_HOUR, 3 * MS_PER_HOUR), (1 * MS_PER_DAY, 6 * MS_PER_HOUR)
    ];

    /// <summary>
    /// The smallest clock-friendly major spacing that keeps the visible range within
    /// <paramref name="maxIntervalCount"/> intervals, and within one interval per
    /// <paramref name="maxIntervalSize"/> pixels of <paramref name="availableSize"/>.
    /// </summary>
    /// <param name="rangeMs">Visible range, in milliseconds.</param>
    /// <param name="availableSize">Length of the axis, in pixels. Zero or less means "not laid out yet".</param>
    /// <param name="maxIntervalSize">Fewest pixels one interval may take (OxyPlot's <c>IntervalLength</c>).</param>
    /// <param name="maxIntervalCount">Upper bound on the number of intervals (OxyPlot's <c>MaximumMajorIntervalCount</c>).</param>
    public static double ChooseMajorStep(
        double rangeMs, double availableSize, double maxIntervalSize, double maxIntervalCount = double.MaxValue)
    {
        if (!double.IsFinite(rangeMs) || rangeMs <= 0)
        {
            return MS_PER_SECOND;
        }

        var fitCount = availableSize > 0 && maxIntervalSize > 0 ? availableSize / maxIntervalSize : 2;
        var maxCount = Math.Max(Math.Min(maxIntervalCount, fitCount), 1);

        foreach (var (major, _) in Steps)
        {
            if (rangeMs / major <= maxCount)
            {
                return major;
            }
        }

        // Past a day per tick (a range of weeks on a narrow plot): whole days in a 1-2-5 sequence.
        var days = 1.0;
        while (rangeMs / (days * MS_PER_DAY) > maxCount)
        {
            days = NextOneTwoFive(days);
        }

        return days * MS_PER_DAY;
    }

    /// <summary>The minor spacing drawn between ticks <paramref name="majorStepMs"/> apart.</summary>
    public static double MinorStepFor(double majorStepMs)
    {
        foreach (var (major, minor) in Steps)
        {
            if (major == majorStepMs)
            {
                return minor;
            }
        }

        // A whole number of days from ChooseMajorStep, or a step set by hand: a quarter of a 2-step, a
        // fifth of anything else, as OxyPlot's own decimal minor interval does — counted in days for a
        // day-multiple, so 2 days divides into 12 h rather than 9.6 h.
        var unit = majorStepMs >= MS_PER_DAY && majorStepMs % MS_PER_DAY == 0 ? MS_PER_DAY : 1;
        var count = majorStepMs / unit;
        var mantissa = count / Math.Pow(10, Math.Floor(Math.Log10(count)));
        return Math.Abs(mantissa - 2) < 1e-9 ? majorStepMs / 4 : majorStepMs / 5;
    }

    /// <summary>
    /// True when the visible range reaches an hour, so every label in it carries the hour field.
    /// Decided per range, not per label, so that one axis never mixes <c>59:00</c> with <c>1:00:00</c>.
    /// </summary>
    public static bool NeedsHours(double minimumMs, double maximumMs) =>
        Math.Max(Math.Abs(minimumMs), Math.Abs(maximumMs)) >= MS_PER_HOUR;

    /// <summary>
    /// Digits after the decimal point that tell ticks <paramref name="majorStepMs"/> apart: none at
    /// one second or more, then one, two or three as the spacing drops below 1 s, 100 ms and 10 ms.
    /// </summary>
    public static int FractionDigitsFor(double majorStepMs) =>
        majorStepMs >= 1_000 ? 0 : majorStepMs >= 100 ? 1 : majorStepMs >= 10 ? 2 : 3;

    /// <summary>
    /// Writes <paramref name="valueMs"/> as <c>m:ss</c>, or <c>h:mm:ss</c> when
    /// <paramref name="showHours"/>, with as many fraction digits as ticks
    /// <paramref name="majorStepMs"/> apart need. Never scientific notation, at any value.
    /// </summary>
    /// <example><c>Format(36_000_000, 3_600_000, true)</c> is <c>10:00:00</c>;
    /// <c>Format(65_250, 250, false)</c> is <c>1:05.3</c>.</example>
    public static string Format(double valueMs, double majorStepMs, bool showHours, IFormatProvider? provider = null)
    {
        if (!double.IsFinite(valueMs))
        {
            return string.Empty;
        }

        var digits = FractionDigitsFor(majorStepMs);
        long unitsPerSecond = digits switch { 0 => 1, 1 => 10, 2 => 100, _ => 1_000 };

        // Round ONCE, in the smallest unit shown, then split with integer arithmetic. Rounding each
        // field separately would let a tick computed as 59999.99998 ms read 0:59 instead of 1:00.
        var units = (long)Math.Round(Math.Abs(valueMs) / MS_PER_SECOND * unitsPerSecond, MidpointRounding.AwayFromZero);
        var totalSeconds = units / unitsPerSecond;
        var fraction = units % unitsPerSecond;

        var text = new StringBuilder(16);
        if (valueMs < 0 && units != 0)
        {
            text.Append('-');
        }

        if (showHours)
        {
            text.Append(totalSeconds / 3_600)
                .Append(':')
                .Append((totalSeconds / 60 % 60).ToString("00", CultureInfo.InvariantCulture));
        }
        else
        {
            text.Append(totalSeconds / 60);
        }

        text.Append(':').Append((totalSeconds % 60).ToString("00", CultureInfo.InvariantCulture));

        if (digits > 0)
        {
            var separator = NumberFormatInfo.GetInstance(provider ?? CultureInfo.CurrentCulture).NumberDecimalSeparator;
            text.Append(separator).Append(fraction.ToString(new string('0', digits), CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// One instant to the millisecond, for the hover tracker: <c>m:ss.fff</c>, or <c>h:mm:ss.fff</c>
    /// from an hour on. The tracker names a point, so it shows the point's own precision rather than
    /// the axis's tick precision.
    /// </summary>
    public static string FormatPrecise(double valueMs, IFormatProvider? provider = null) =>
        Format(valueMs, majorStepMs: 1, showHours: Math.Abs(valueMs) >= MS_PER_HOUR, provider);

    private static double NextOneTwoFive(double value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var mantissa = Math.Round(value / magnitude);
        return mantissa switch
        {
            < 2 => 2 * magnitude,
            < 5 => 5 * magnitude,
            _ => 10 * magnitude
        };
    }
}
