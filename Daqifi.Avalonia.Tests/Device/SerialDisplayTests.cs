using System.Globalization;
using System.Xml.Linq;
using Daqifi.Desktop.Converters;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using OxyPlot;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins issue #493: the app shows a serial in the hex form <c>*IDN?</c> reports, while every stored
/// serial — the session key, the profile identity — stays the decimal string Core produces.
///
/// <para>
/// The reference values are read off real hardware, not derived from the code under test. The bench
/// Nq1 answers <c>*IDN?</c> with <c>DAQiFi,Nq1,7E2815916200E898,01-02</c> over USB, and the app
/// stores its serial as <c>9090539562006014104</c> (the protobuf <c>DeviceSn</c>, rendered base-10).
/// The firmware builds that third field with <c>"%016llX"</c>, so the display form is 16 upper-case
/// hex digits, zero-padded, with no prefix.
/// </para>
/// </summary>
public class SerialDisplayTests
{
    private const string BenchDecimal = "9090539562006014104";
    private const string BenchIdnHex = "7E2815916200E898";

    #region ToDisplay

    [Theory]
    // The bench Nq1: decimal as stored, hex as *IDN? reports it.
    [InlineData(BenchDecimal, BenchIdnHex)]
    // The example in issue #493.
    [InlineData("9090642310508636305", "7E2873046200E891")]
    // Zero-padded to the full 16 digits, as "%016llX" does.
    [InlineData("1024", "0000000000000400")]
    [InlineData("0", "0000000000000000")]
    [InlineData("18446744073709551615", "FFFFFFFFFFFFFFFF")]
    public void A_decimal_serial_is_shown_as_the_IDN_hex_form(string stored, string expected)
    {
        Assert.Equal(expected, DeviceSerials.ToDisplay(stored));
    }

    [Theory]
    // Not numbers at all: a USB descriptor, a placeholder, a test fixture.
    [InlineData("SN-0001")]
    [InlineData("Unknown")]
    [InlineData("DAQiFi-Nq1-0004104")]
    // Already hex: shown as written, never converted a second time.
    [InlineData(BenchIdnHex)]
    // Not a plain base-10 number: sign, padding, separators, and a value past 64 bits.
    [InlineData("-1")]
    [InlineData(" 1024")]
    [InlineData("1,024")]
    [InlineData("18446744073709551616")]
    [InlineData("")]
    public void A_serial_that_is_not_a_plain_decimal_number_is_shown_unchanged(string stored)
    {
        Assert.Equal(stored, DeviceSerials.ToDisplay(stored));
    }

    [Fact]
    public void A_missing_serial_is_shown_as_an_empty_string()
    {
        Assert.Equal(string.Empty, DeviceSerials.ToDisplay(null));
    }

    [Theory]
    [InlineData(BenchDecimal, "...E898")]
    [InlineData("SN-0001", "...0001")]
    [InlineData("A104", "A104")]
    [InlineData(null, "")]
    public void The_short_form_is_the_last_four_characters_of_the_display_form(string? stored, string expected)
    {
        Assert.Equal(expected, DeviceSerials.ToShortDisplay(stored));
    }

    #endregion

    #region Matching — the source of a serial sets its format (review thread on DeviceSerials.cs)

    /// <summary>
    /// The defect the review found. A 16-digit hex serial that has only digits in it is also a
    /// valid decimal number. Core's <c>DeviceIdentity</c> reads such a value as decimal first, so
    /// the hex form of stored serial <c>1024</c> (<c>0000000000000400</c>) was read as <c>400</c>:
    /// the correct board was rejected and board <c>400</c> was accepted. Core 1.9.1 has no way to
    /// say which format a value is in, so the app decides from where the value came from.
    /// </summary>
    /// <remarks>
    /// <see cref="DeviceSerials.MatchesScpiSerial"/> reads the stored side as decimal and the SCPI
    /// side as hex, always. These are the reviewer's two cases: the correct match and the incorrect
    /// match, for the serial whose hex form has only digits.
    /// </remarks>
    [Fact]
    public void An_all_digit_SCPI_serial_matches_the_stored_serial_it_was_made_from()
    {
        Assert.Equal("0000000000000400", DeviceSerials.ToDisplay("1024"));
        Assert.True(DeviceSerials.MatchesScpiSerial("1024", "0000000000000400"));
    }

    [Fact]
    public void An_all_digit_SCPI_serial_does_not_match_the_board_its_digits_spell_in_decimal()
    {
        Assert.False(DeviceSerials.MatchesScpiSerial("400", "0000000000000400"));
    }

    /// <summary>
    /// The same ambiguity, for two STORED serials: the profile matcher, the drawer and the notice
    /// de-duplication all compare one stored serial with another. A stored serial is decimal text
    /// that the app wrote, so it is compared as text and never read as hex. The hex text of board
    /// <c>1024</c>, copied into a saved profile, therefore selects no board by serial — and above
    /// all not board <c>400</c>, which Core's decimal-first rule selected.
    /// </summary>
    [Theory]
    [InlineData("400", "0000000000000400")]
    [InlineData("0000000000000400", "400")]
    [InlineData("1024", "0000000000000400")]
    public void A_stored_serial_is_never_read_as_hex(string left, string right)
    {
        Assert.False(DeviceSerials.SameDevice(left, right));
    }

    /// <summary>
    /// The acceptance criterion of #493, now through the source-aware rule: what the screen shows
    /// (the SCPI form) still names the board it was made from, for every kind of stored serial —
    /// including the ones that have no hex form and are shown unchanged.
    /// </summary>
    [Theory]
    [InlineData(BenchDecimal)]
    [InlineData("9090642310508636305")]
    [InlineData("1024")]
    [InlineData("0")]
    [InlineData("18446744073709551615")]
    [InlineData("SN-0001")]
    [InlineData("DAQiFi-Nq1-0004104")]
    public void The_display_form_of_a_stored_serial_matches_that_stored_serial(string stored)
    {
        Assert.True(DeviceSerials.MatchesScpiSerial(stored, DeviceSerials.ToDisplay(stored)));
    }

    [Theory]
    // The bench Nq1, as *IDN? reports it, in either case.
    [InlineData(BenchDecimal, BenchIdnHex)]
    [InlineData(BenchDecimal, "7e2815916200e898")]
    // The example in issue #493.
    [InlineData("9090642310508636305", "7E2873046200E891")]
    public void The_IDN_serial_of_a_board_matches_its_stored_serial(string stored, string scpi)
    {
        Assert.True(DeviceSerials.MatchesScpiSerial(stored, scpi));
    }

    [Theory]
    // Two real boards, crossed.
    [InlineData(BenchDecimal, "7E2873046200E891")]
    [InlineData("9090642310508636305", BenchIdnHex)]
    // A SCPI serial is hex, so the decimal text of the same board is not its SCPI form.
    [InlineData(BenchDecimal, BenchDecimal)]
    // A missing serial names no board.
    [InlineData(null, BenchIdnHex)]
    [InlineData(BenchDecimal, null)]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void A_SCPI_serial_does_not_match_a_different_or_missing_stored_serial(string? stored, string? scpi)
    {
        Assert.False(DeviceSerials.MatchesScpiSerial(stored, scpi));
    }

    [Theory]
    // Two real boards: stored serials of different boards are different text.
    [InlineData(BenchDecimal, "9090642310508636305")]
    [InlineData(BenchIdnHex, "7E2873046200E891")]
    public void The_stored_serials_of_two_different_boards_are_not_the_same_device(string left, string right)
    {
        Assert.False(DeviceSerials.SameDevice(left, right));
    }

    /// <summary>
    /// Two stored serials compare by the rule <c>main</c> had before #493: equal text, without
    /// regard to case. That is exact for every serial the app stores, because each one is the
    /// base-10 text of one <c>ulong</c>, which has exactly one spelling.
    /// </summary>
    [Theory]
    [InlineData(BenchDecimal, BenchDecimal, true)]
    [InlineData("sn-0001", "SN-0001", true)]
    [InlineData(null, null, true)]
    [InlineData("", "", true)]
    [InlineData(null, BenchDecimal, false)]
    [InlineData(BenchDecimal, null, false)]
    [InlineData("", BenchDecimal, false)]
    [InlineData(" ", "", false)]
    [InlineData("007", "7", false)]
    public void Two_stored_serials_keep_the_rule_from_before_493(string? left, string? right, bool expected)
    {
        Assert.Equal(expected, DeviceSerials.SameDevice(left, right));
    }

    #endregion

    #region The converter, and every serial binding goes through it

    [Theory]
    [InlineData(BenchDecimal, BenchIdnHex)]
    [InlineData("SN-0001", "SN-0001")]
    [InlineData(null, "")]
    public void The_converter_applies_the_display_rule(string? stored, string expected)
    {
        var converter = new SerialDisplayConverter();

        Assert.Equal(expected, converter.Convert(stored, typeof(string), null, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The binding paths that carry a stored serial to the screen. <c>DeviceSerialNo</c> is the
    /// device, channel and profile property; <c>SerialNo</c> is the new-profile row; and
    /// <c>SerialNumber</c> is the device tile.
    /// </summary>
    private static readonly string[] SerialMembers = ["DeviceSerialNo", "SerialNo", "SerialNumber"];

    /// <summary>
    /// Every binding to a serial member in every view goes through <c>SerialDisplay</c>. A new view
    /// that binds a serial without it would show the decimal form again, and nothing else would
    /// notice — the build is green and the stored value is right.
    /// </summary>
    /// <remarks>
    /// The markup is parsed, not grepped, so a comment that mentions a binding cannot count as one.
    /// </remarks>
    [Fact]
    public void Every_binding_to_a_serial_goes_through_the_display_converter()
    {
        var (bindings, unconverted) = SerialBindings();

        Assert.True(
            unconverted.Count == 0,
            "Serial bindings that show the stored form:" + Environment.NewLine + string.Join(Environment.NewLine, unconverted));

        // Positive control: the walk must find the serial bindings it is guarding. There are 12 today
        // (connection dialog 2, profiles desktop and mobile 4, devices desktop and mobile 4, live
        // legend 1, logged legend tooltip 1); the bound is far enough below that to survive churn
        // while still failing if the walk collapses.
        Assert.True(bindings >= 8, $"Only {bindings} serial binding(s) were found.");
    }

    private static (int Bindings, List<string> Unconverted) SerialBindings()
    {
        var root = BindingFacts.RepoRoot();
        var bindings = 0;
        var unconverted = new List<string>();

        foreach (var path in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.Contains("/bin/", StringComparison.Ordinal) ||
                relative.StartsWith("third_party/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var attribute in XDocument.Parse(File.ReadAllText(path)).Descendants().Attributes())
            {
                var value = attribute.Value.Trim();
                if (!value.StartsWith("{Binding ", StringComparison.Ordinal))
                {
                    continue;
                }

                var bindingPath = value["{Binding ".Length..].Split([',', '}'])[0].Trim();
                var member = bindingPath.Split('.')[^1];
                if (!SerialMembers.Contains(member, StringComparer.Ordinal))
                {
                    continue;
                }

                bindings++;
                if (!value.Contains("Converter={StaticResource SerialDisplay}", StringComparison.Ordinal))
                {
                    unconverted.Add($"{relative}: {attribute.Name.LocalName}=\"{attribute.Value}\"");
                }
            }
        }

        return (bindings, unconverted);
    }

    #endregion

    #region The code-built display strings

    [Fact]
    public void The_logged_plot_tracker_names_the_device_by_its_hex_tail()
    {
        var (series, legendItem) = new PlotModelFactory().CreateChannelSeries(
            "AI0", BenchDecimal, Daqifi.Core.Channel.ChannelType.Analog, "#FFD32F2F", new PlotModel(), null);

        Assert.StartsWith("AI0 (...E898)", series.TrackerFormatString);
        Assert.Equal("...E898", legendItem.TruncatedSerialNo);
        // The identity the legend keys on is still the stored value.
        Assert.Equal(BenchDecimal, legendItem.DeviceSerialNo);
    }

    [Fact]
    public void The_logged_legend_group_shows_the_hex_tail_and_keeps_the_stored_serial()
    {
        var group = new DeviceLegendGroup(BenchDecimal);

        Assert.Equal("...E898", group.TruncatedSerialNo);
        Assert.Equal(BenchDecimal, group.DeviceSerialNo);
    }

    [Fact]
    public void A_multi_device_session_names_each_rate_by_its_hex_tail()
    {
        var session = new LoggingSession
        {
            DeviceMetadata =
            [
                new SessionDeviceMetadata { DeviceSerialNo = BenchDecimal, DeviceName = "A", SamplingFrequencyHz = 100 },
                new SessionDeviceMetadata { DeviceSerialNo = "9090642310508636305", DeviceName = "B", SamplingFrequencyHz = 1000 },
            ],
        };

        Assert.Equal("...E898: 100 Hz · ...E891: 1 kHz", session.FrequencyDisplay);
    }

    [Fact]
    public void The_summary_section_header_shows_the_hex_serial()
    {
        var summary = new SummaryLogger.DeviceSummary("Nq1", BenchDecimal, null!, "-");

        Assert.Equal($"Nq1  (S/N {BenchIdnHex})", summary.Title);
        Assert.Equal(BenchDecimal, summary.SerialNumber);
    }

    [Fact]
    public void The_duplicate_device_dialog_shows_the_hex_serial()
    {
        var existing = new RecordingStreamingDevice(BenchDecimal);
        var incoming = new RecordingStreamingDevice(BenchDecimal);

        var dialog = new DuplicateDeviceDialogViewModel(existing, incoming, "USB", "WiFi");

        Assert.StartsWith($"This device (S/N: {BenchIdnHex}) is already connected via USB.", dialog.Message);
        Assert.Equal(BenchDecimal, dialog.DeviceSerialNumber);
    }

    #endregion
}
