using System.Text;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Microsoft.EntityFrameworkCore;
using OxyPlot;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins the rule for serials in Logged Data (PR #551, issue #493): a logged session shows the serial
/// text it stored, unchanged from <c>main</c>. Only a live device shows the <c>*IDN?</c> hex form.
/// </summary>
/// <remarks>
/// <para>
/// A logged session's stored serial does not show its format. A live-recorded session stores the
/// decimal text Core makes from the protobuf <c>DeviceSn</c>. A session imported from an SD-card CSV
/// or JSON log stores the header text, which the firmware writes with <c>"%016llX"</c>
/// (<c>csv_encoder.c</c>), and Core's parsers keep it. The text <c>0000000000000400</c> is valid hex
/// and valid decimal, and the session row keeps no record of its source. So Logged Data does not
/// convert: it shows the stored text, the last four characters in the legend and the tracker.
/// </para>
/// <para>
/// The user decided this on 2026-11-11 ("go with option 3 for #551"). Hex in Logged Data is open
/// as #553.
/// </para>
/// <para>
/// Each test goes from the real writer (the SD importer or the live sample writer) through the
/// database to the three Logged Data display members: <see cref="DeviceLegendGroup.TruncatedSerialNo"/>,
/// <see cref="LoggedSeriesLegendItem.TruncatedSerialNo"/> and the tracker text that
/// <see cref="PlotModelFactory.CreateChannelSeries"/> builds.
/// </para>
/// </remarks>
public sealed class LoggedDataSerialTextTests : IDisposable
{
    private const string BenchDecimal = "9090539562006014104";
    private const string BenchIdnHex = "7E2815916200E898";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "logged-serial-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public LoggedDataSerialTextTests()
    {
        Directory.CreateDirectory(_directory);
        DatabaseMigrator.ApplyMigrations(Factory(), DatabasePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// The reviewer's case on PR #551: an SD-card header serial made only of digits. Before the
    /// change, Logged Data read it as decimal 400 and showed <c>...0190</c>.
    /// </summary>
    [Fact]
    public async Task An_SD_card_session_with_an_all_digit_hex_serial_shows_the_stored_text()
    {
        var stored = await ImportCsvWithSerial("0000000000000400");

        Assert.Equal("0000000000000400", stored);
        AssertLoggedDataShows(stored, "...0400");
    }

    [Fact]
    public async Task An_SD_card_session_with_a_hex_serial_that_has_a_letter_shows_the_stored_text()
    {
        var stored = await ImportCsvWithSerial(BenchIdnHex);

        Assert.Equal(BenchIdnHex, stored);
        AssertLoggedDataShows(stored, "...E898");
    }

    /// <summary>
    /// The other direction: a live-recorded session stores the decimal serial, and Logged Data shows
    /// that decimal text, as on <c>main</c>. It does not convert it to the hex form.
    /// </summary>
    [Fact]
    public void A_live_recorded_session_shows_the_stored_decimal_text()
    {
        using (var context = Factory().CreateDbContext())
        {
            context.Sessions.Add(new LoggingSession { ID = 1, Name = "Live", SessionStart = DateTime.UtcNow });
            context.SaveChanges();
        }

        using (var writer = new SessionSampleWriter(Factory(), new RecordingAppLogger()))
        {
            writer.Add(new DataSample
            {
                LoggingSessionID = 1,
                Value = 1.5,
                TimestampTicks = DateTime.UtcNow.Ticks,
                DeviceName = "Nq1",
                ChannelName = "AI0",
                DeviceSerialNo = BenchDecimal,
                Color = "#FF2196F3",
            });
            writer.WaitForIdle(TimeSpan.FromSeconds(10));
        }

        var stored = StoredSerials(1);

        Assert.Equal([BenchDecimal], stored);
        AssertLoggedDataShows(stored[0], "...4104");
    }

    /// <summary>
    /// The session list's rate line names each device of a multi-device session by the tail of its
    /// stored serial, as on <c>main</c>.
    /// </summary>
    [Fact]
    public void A_multi_device_session_names_each_rate_by_the_tail_of_its_stored_serial()
    {
        var session = new LoggingSession
        {
            DeviceMetadata =
            [
                new SessionDeviceMetadata { DeviceSerialNo = BenchDecimal, DeviceName = "A", SamplingFrequencyHz = 100 },
                new SessionDeviceMetadata { DeviceSerialNo = "9090642310508636305", DeviceName = "B", SamplingFrequencyHz = 1000 },
            ],
        };

        Assert.Equal("...4104: 100 Hz · ...6305: 1 kHz", session.FrequencyDisplay);
    }

    /// <summary>
    /// The boundary of the short form, as on <c>main</c>: a stored serial of four characters or fewer
    /// is shown whole, and five is the shortest that gets the ellipsis.
    /// </summary>
    [Theory]
    [InlineData("4104", "4104")]
    [InlineData("4", "4")]
    [InlineData("41041", "...1041")]
    public void A_stored_serial_of_four_characters_or_fewer_is_shown_whole(string stored, string expected)
    {
        AssertLoggedDataShows(stored, expected);
    }

    [Fact]
    public void The_rate_line_shows_a_short_stored_serial_whole()
    {
        var session = new LoggingSession
        {
            DeviceMetadata =
            [
                new SessionDeviceMetadata { DeviceSerialNo = "4104", DeviceName = "A", SamplingFrequencyHz = 100 },
                new SessionDeviceMetadata { DeviceSerialNo = "9090642310508636305", DeviceName = "B", SamplingFrequencyHz = 1000 },
            ],
        };

        Assert.Equal("4104: 100 Hz · ...6305: 1 kHz", session.FrequencyDisplay);
    }

    private static void AssertLoggedDataShows(string stored, string expectedTail)
    {
        var (series, legendItem) = new PlotModelFactory().CreateChannelSeries(
            "AI0", stored, Daqifi.Core.Channel.ChannelType.Analog, "#FFD32F2F", new PlotModel(), null);
        var group = new DeviceLegendGroup(stored);

        Assert.Equal(expectedTail, group.TruncatedSerialNo);
        Assert.Equal(expectedTail, legendItem.TruncatedSerialNo);
        Assert.StartsWith($"AI0 ({expectedTail})", series.TrackerFormatString);

        // The identity the legend keys on is the stored value.
        Assert.Equal(stored, group.DeviceSerialNo);
        Assert.Equal(stored, legendItem.DeviceSerialNo);
    }

    private async Task<string> ImportCsvWithSerial(string serial)
    {
        var path = Path.Combine(_directory, "log_20261111_120000.csv");
        File.WriteAllText(
            path,
            "# Device: Nyquist 1\n" +
            $"# Serial Number: {serial}\n" +
            "# Timestamp Tick Rate: 50000000 Hz\n" +
            "ain0_ts,ain0_val\n" +
            "1000,100\n" +
            "2000,101\n",
            Encoding.ASCII);

        var result = await new SdCardSessionImporter(
            Factory(), SdCardSessionImporter.DOWNLOAD_STALL_TIMEOUT, new RecordingAppLogger()).ImportFromFileAsync(path);

        Assert.True(result.SessionPersisted);
        var stored = StoredSerials(result.Session.ID);
        Assert.Single(stored);
        return stored[0];
    }

    private List<string> StoredSerials(int sessionId)
    {
        using var context = Factory().CreateDbContext();
        return context.Samples
            .Where(s => s.LoggingSessionID == sessionId)
            .Select(s => s.DeviceSerialNo)
            .Distinct()
            .ToList();
    }

    private IDbContextFactory<LoggingContext> Factory() => TestDatabase.Contexts(DatabasePath);
}
