using System.Text;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins the one stored serial that is not decimal (issue #493, <see cref="DeviceSerials"/> remarks).
/// </summary>
/// <remarks>
/// <para>
/// The firmware writes the SD-card log header with <c>"# Serial Number: %016llX\n"</c>
/// (<c>csv_encoder.c</c>), and Core's CSV parser keeps that text as it is. So a session imported
/// from an SD-card log stores the hex form, while a live-logged session of the same board stores the
/// decimal form. <see cref="DeviceSerials.ToDisplay"/> shows the hex form unchanged when it has a
/// letter in it, which is the case for the bench Nq1.
/// </para>
/// <para>
/// If Core or the importer starts to convert this serial to decimal, the first assertion fails, and
/// the <see cref="DeviceSerials"/> remarks about SD-card serials must change with it.
/// </para>
/// </remarks>
public sealed class SdCardImportSerialTests : IDisposable
{
    private const string BenchIdnHex = "7E2815916200E898";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "sd-import-serial-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public SdCardImportSerialTests()
    {
        Directory.CreateDirectory(_directory);
        DatabaseMigrator.ApplyMigrations(Factory(), DatabasePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task A_session_imported_from_an_SD_card_log_stores_the_header_hex_and_shows_it_unchanged()
    {
        var path = Path.Combine(_directory, "log_20261111_120000.csv");
        File.WriteAllText(
            path,
            "# Device: Nyquist 1\n" +
            $"# Serial Number: {BenchIdnHex}\n" +
            "# Timestamp Tick Rate: 50000000 Hz\n" +
            "ain0_ts,ain0_val\n" +
            "1000,100\n" +
            "2000,101\n",
            Encoding.ASCII);

        var result = await new SdCardSessionImporter(
            Factory(), SdCardSessionImporter.DOWNLOAD_STALL_TIMEOUT, new RecordingAppLogger()).ImportFromFileAsync(path);

        Assert.True(result.SessionPersisted);

        using var context = Factory().CreateDbContext();
        var stored = context.Samples
            .Where(s => s.LoggingSessionID == result.Session.ID)
            .Select(s => s.DeviceSerialNo)
            .Distinct()
            .ToList();

        Assert.Equal([BenchIdnHex], stored);
        Assert.Equal(BenchIdnHex, DeviceSerials.ToDisplay(stored[0]));
        Assert.Equal("...E898", DeviceSerials.ToShortDisplay(stored[0]));
    }

    private IDbContextFactory<LoggingContext> Factory() => TestDatabase.Contexts(DatabasePath);
}
