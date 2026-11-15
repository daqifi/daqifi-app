using System.Globalization;
using Daqifi.Desktop.Helpers;
using Xunit;

namespace Daqifi.Avalonia.Tests.Helpers;

/// <summary>
/// Issue #585: the plots' time axis wrote elapsed milliseconds with the general number format, which
/// turns into scientific notation at 1,000,000 ms — a ten-hour session read <c>0, 2e+06 … 3.6e+07</c>.
/// These tests pin the replacement arithmetic: clock labels, clock-friendly tick spacing, and no
/// exponent at any value a session can reach.
/// </summary>
public class ElapsedTimeFormatTests
{
    private const double Second = ElapsedTimeFormat.MS_PER_SECOND;
    private const double Minute = ElapsedTimeFormat.MS_PER_MINUTE;
    private const double Hour = ElapsedTimeFormat.MS_PER_HOUR;

    private static string Format(double valueMs, double stepMs, bool showHours) =>
        ElapsedTimeFormat.Format(valueMs, stepMs, showHours, CultureInfo.InvariantCulture);

    /// <summary>The values the issue names, plus its boundary, at the step the axis would use for each.</summary>
    [Theory]
    [InlineData(0, 10_000, false, "0:00")]
    [InlineData(999_999, 60_000, false, "16:40")]
    [InlineData(1_000_000, 60_000, false, "16:40")]
    [InlineData(1_200_000, 120_000, false, "20:00")]
    [InlineData(36_000_000, 3_600_000, true, "10:00:00")]
    [InlineData(0, 3_600_000, true, "0:00:00")]
    [InlineData(5_400_000, 1_800_000, true, "1:30:00")]
    [InlineData(65_250, 250, false, "1:05.3")]
    [InlineData(65_250, 50, false, "1:05.25")]
    [InlineData(1_234, 2, false, "0:01.234")]
    [InlineData(18_000_200, 200, true, "5:00:00.2")]
    [InlineData(0.35, 0.05, false, "0:00.00035")]
    [InlineData(18_000_000.0015, 0.0005, true, "5:00:00.0000015")]
    public void A_value_reads_as_a_clock(double valueMs, double stepMs, bool showHours, string expected)
    {
        Assert.Equal(expected, Format(valueMs, stepMs, showHours));
    }

    /// <summary>
    /// The defect itself: nothing from zero to well past any real session may contain an exponent.
    /// Walks 0 ms to 100 hours on a ragged step, at every tick precision the axis can choose.
    /// </summary>
    [Fact]
    public void No_value_up_to_a_hundred_hours_is_written_in_scientific_notation()
    {
        foreach (var step in new[] { 1.0, 10, 100, 1_000, Minute, Hour })
        {
            for (var value = 0.0; value <= 100 * Hour; value = value * 1.7 + 0.37)
            {
                var text = Format(value, step, ElapsedTimeFormat.NeedsHours(0, value));
                Assert.DoesNotContain("e", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// A tick OxyPlot computes as a multiple of the step carries floating-point error. Rounding once,
    /// in the smallest unit shown, keeps 59999.99998 ms reading as 1:00 rather than 0:59 or 0:60.
    /// </summary>
    [Theory]
    [InlineData(59_999.99998, 10_000, false, "1:00")]
    [InlineData(3_599_999.9999, 600_000, true, "1:00:00")]
    [InlineData(299.99999999, 100, false, "0:00.3")]
    public void Floating_point_error_in_a_tick_does_not_reach_the_label(
        double valueMs, double stepMs, bool showHours, string expected)
    {
        Assert.Equal(expected, Format(valueMs, stepMs, showHours));
    }

    /// <summary>A pan can show a little before the first sample; that side reads as negative, and zero never as "-0".</summary>
    [Theory]
    [InlineData(-5_000, 5_000, false, "-0:05")]
    [InlineData(-0.0001, 1_000, false, "0:00")]
    [InlineData(-3_600_000, 3_600_000, true, "-1:00:00")]
    public void Negative_values_carry_a_sign_and_zero_does_not(
        double valueMs, double stepMs, bool showHours, string expected)
    {
        Assert.Equal(expected, Format(valueMs, stepMs, showHours));
    }

    /// <summary>Hours keep counting past a day rather than wrapping, as TimeSpan's hh would.</summary>
    [Fact]
    public void Hours_keep_counting_past_a_day()
    {
        Assert.Equal("26:00:00", Format(26 * Hour, Hour, showHours: true));
    }

    [Fact]
    public void The_fraction_uses_the_cultures_decimal_separator()
    {
        Assert.Equal("1:05,3", ElapsedTimeFormat.Format(65_250, 250, false, CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void The_tracker_value_is_precise_to_the_millisecond_and_gains_hours_at_one_hour()
    {
        Assert.Equal("12:34.567", ElapsedTimeFormat.FormatPrecise(12 * Minute + 34_567, CultureInfo.InvariantCulture));
        Assert.Equal("10:00:00.000", ElapsedTimeFormat.FormatPrecise(10 * Hour, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Review on #594: two samples of a 20 kHz recording, 0.05 ms apart, must not share one tracker
    /// time. The tracker keeps the digits the timestamp has, down to its 0.1 µs resolution.
    /// </summary>
    [Theory]
    [InlineData(0.05, "0:00.00005")]
    [InlineData(0.10, "0:00.0001")]
    [InlineData(1_234.0001, "0:01.2340001")]
    [InlineData(5 * 3_600_000.0 + 0.05, "5:00:00.00005")]
    public void The_tracker_value_keeps_the_sub_millisecond_digits_a_sample_has(double valueMs, string expected)
    {
        Assert.Equal(expected, ElapsedTimeFormat.FormatPrecise(valueMs, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(1_000, 0)]
    [InlineData(500, 1)]
    [InlineData(100, 1)]
    [InlineData(50, 2)]
    [InlineData(10, 2)]
    [InlineData(1, 3)]
    [InlineData(0.5, 4)]
    [InlineData(0.05, 5)]
    [InlineData(0.0001, 7)]
    [InlineData(0.00001, 7)]
    public void A_finer_step_gets_one_more_digit_per_tenfold_drop(double stepMs, int expected)
    {
        Assert.Equal(expected, ElapsedTimeFormat.FractionDigitsFor(stepMs));
    }

    [Theory]
    [InlineData(0, 59 * 60_000, false)]
    [InlineData(0, 3_600_000, true)]
    [InlineData(-3_600_000, 0, true)]
    public void Hours_show_once_the_visible_range_reaches_an_hour(double min, double max, bool expected)
    {
        Assert.Equal(expected, ElapsedTimeFormat.NeedsHours(min, max));
    }

    /// <summary>
    /// The spacing for each duration a user can reach on a 1,200 px plot (15 intervals of 80 px), from a
    /// zoom of a few milliseconds to a ten-hour session. Every one is a step a clock divides into
    /// evenly, never a decimal step such as the 5,000,000 ms that put a tick at 1:23:20.
    /// </summary>
    [Theory]
    [InlineData(0.001, 0.0001)]                 // the narrowest zoom: 1 µs
    [InlineData(1, 0.1)]                        // 20 samples of a 20 kHz recording
    [InlineData(10, 1)]
    [InlineData(3 * Second, 200)]
    [InlineData(10 * Second, 1 * Second)]
    [InlineData(60 * Second, 5 * Second)]       // the empty frame
    [InlineData(17 * Minute, 2 * Minute)]
    [InlineData(60 * Minute, 5 * Minute)]
    [InlineData(10.2 * Hour, 1 * Hour)]         // the session-10h plot, with padding
    [InlineData(40 * Hour, 3 * Hour)]
    [InlineData(30 * 24 * Hour, 2 * 24 * Hour)]  // a month, past the table: whole days
    public void The_major_step_is_a_clock_step_that_fits_the_plot(double rangeMs, double expectedStepMs)
    {
        Assert.Equal(expectedStepMs, ElapsedTimeFormat.ChooseMajorStep(rangeMs, availableSize: 1200, maxIntervalSize: 80));
    }

    [Theory]
    [InlineData(1 * Minute, 15 * Second)]
    [InlineData(1 * Hour, 15 * Minute)]
    [InlineData(500, 100)]
    [InlineData(2 * 24 * Hour, 12 * Hour)]
    public void Minor_gridlines_divide_a_major_step_evenly(double majorMs, double expectedMinorMs)
    {
        var minor = ElapsedTimeFormat.MinorStepFor(majorMs);

        Assert.Equal(expectedMinorMs, minor);
        Assert.Equal(0, majorMs % minor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void A_degenerate_range_still_gets_a_usable_step(double rangeMs)
    {
        Assert.Equal(Second, ElapsedTimeFormat.ChooseMajorStep(rangeMs, 1200, 80));
    }
}
