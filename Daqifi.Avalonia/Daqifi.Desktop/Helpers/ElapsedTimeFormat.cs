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
    /// The finest tick spacing: 0.1 µs, which is the resolution of a sample timestamp (.NET ticks of
    /// 100 ns). A zoom never needs ticks closer than the samples can be told apart.
    /// </summary>
    public const double FINEST_STEP_MS = 0.0001;

    /// <summary>The most fraction digits a label carries: the 7 that write 0.1 µs in seconds.</summary>
    public const int MAX_FRACTION_DIGITS = 7;

    /// <summary>
    /// Every major tick spacing the axis may use, smallest first, each with the minor spacing drawn
    /// between its ticks. Below a millisecond the steps are decimal (1, 2, 5 × 10^n down to
    /// <see cref="FINEST_STEP_MS"/>), so a high-rate recording can be zoomed to single samples; from
    /// there on they are the ones a clock divides into evenly (15 s, 30 min, 3 h …), never a decimal
    /// step such as 5,000,000 ms that would put ticks on odd times.
    /// </summary>
    private static readonly (double Major, double Minor)[] Steps =
    [
        (0.0001, 0.00002), (0.0002, 0.00005), (0.0005, 0.0001),
        (0.001, 0.0002), (0.002, 0.0005), (0.005, 0.001),
        (0.01, 0.002), (0.02, 0.005), (0.05, 0.01),
        (0.1, 0.02), (0.2, 0.05), (0.5, 0.1),
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
    /// one second or more, then one more for each tenfold drop (one below 1 s, two below 100 ms,
    /// three below 10 ms, four below 1 ms …), up to <see cref="MAX_FRACTION_DIGITS"/>.
    /// </summary>
    public static int FractionDigitsFor(double majorStepMs)
    {
        if (!double.IsFinite(majorStepMs) || majorStepMs <= 0)
        {
            return 3;
        }

        // The small epsilon keeps an exact power of ten (100 ms) from rounding up to an extra digit.
        var digits = (int)Math.Ceiling(-Math.Log10(majorStepMs / MS_PER_SECOND) - 1e-9);
        return Math.Clamp(digits, 0, MAX_FRACTION_DIGITS);
    }

    /// <summary>
    /// Writes <paramref name="valueMs"/> as <c>m:ss</c>, or <c>h:mm:ss</c> when
    /// <paramref name="showHours"/>, with as many fraction digits as ticks
    /// <paramref name="majorStepMs"/> apart need. Never scientific notation, at any value.
    /// </summary>
    /// <example><c>Format(36_000_000, 3_600_000, true)</c> is <c>10:00:00</c>;
    /// <c>Format(65_250, 250, false)</c> is <c>1:05.3</c>.</example>
    public static string Format(double valueMs, double majorStepMs, bool showHours, IFormatProvider? provider = null) =>
        FormatWithDigits(valueMs, FractionDigitsFor(majorStepMs), showHours, provider);

    private static string FormatWithDigits(double valueMs, int digits, bool showHours, IFormatProvider? provider)
    {
        if (!double.IsFinite(valueMs))
        {
            return string.Empty;
        }

        var unitsPerSecond = 1L;
        for (var i = 0; i < digits; i++)
        {
            unitsPerSecond *= 10;
        }

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
    /// One instant for the hover tracker: <c>m:ss.fff</c>, or <c>h:mm:ss.fff</c> from an hour on, with
    /// more digits when the instant has them — down to 0.1 µs, the timestamp resolution. So a 1 kHz
    /// recording reads <c>0:01.234</c>, and two samples of a 20 kHz recording read <c>0:00.00005</c>
    /// and <c>0:00.0001</c> rather than the same millisecond. The tracker names a point, so it shows
    /// the point's own precision rather than the axis's tick precision.
    /// </summary>
    public static string FormatPrecise(double valueMs, IFormatProvider? provider = null)
    {
        var text = FormatWithDigits(valueMs, MAX_FRACTION_DIGITS, Math.Abs(valueMs) >= MS_PER_HOUR, provider);

        // Trim zeros past the millisecond; the separator and three digits always stay.
        var separator = NumberFormatInfo.GetInstance(provider ?? CultureInfo.CurrentCulture).NumberDecimalSeparator;
        var keep = text.LastIndexOf(separator, StringComparison.Ordinal) + separator.Length + 3;
        var end = text.Length;
        while (end > keep && text[end - 1] == '0')
        {
            end--;
        }

        return text[..end];
    }

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
