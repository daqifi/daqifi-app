using System.Runtime.CompilerServices;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Issue #464: the importer discarded the connected device's timestamp clock and then blamed the
/// firmware for its absence.
///
/// <para>
/// Core resolves an SD log's tick-to-time frequency from three ranked sources — the frequency the
/// file states, then one a live device reports through
/// <see cref="SdCardParseOptions.ConfigurationOverride"/>, then
/// <see cref="SdCardParseOptions.FallbackTimestampFrequency"/> as a guess — and publishes which
/// rung won as <see cref="SdCardLogSession.TimestampFrequencySource"/>.
/// </para>
///
/// <para>
/// The importer's own copy of the parse configuration (<c>BuildDeviceConfiguration</c>) hardcoded
/// <c>TimestampFrequency: 0</c>, which removed the middle rung entirely. That rung is not
/// decorative: firmware 3.7.2 and earlier embed no frequency in SD logs while still reporting one
/// live, so for those files the device was the only accurate source available. Dropping to the
/// guess is not harmless — Core's default was a historical 50 MHz against shipped firmware's
/// 42 MHz, which made every reconstructed timestamp in the session roughly 19% fast, invisibly:
/// the samples still looked evenly spaced.
/// </para>
///
/// <para>
/// #671 deleted that copy. The device now builds the configuration with Core's
/// <c>SdCardDeviceConfiguration.FromDevice</c>, and <c>SdCardParseConfigurationTests</c> pins what
/// it holds for each board. These rows pin the importer's half: the device's clock reaches the
/// parser.
/// </para>
///
/// <para>
/// The warning then read the override rather than the resolved source, so it fired on every
/// device-side import regardless of what the file held, and attributed an app-side defect to the
/// firmware. That misdirection is the reason this nearly became a wrongly-filed firmware ticket.
/// </para>
/// </summary>
public sealed class SdCardImportTimestampFrequencyTests : IDisposable
{
    /// <summary>What shipped Nyquist firmware reports.</summary>
    private const uint DeviceClockHz = 42_000_000;

    /// <summary>
    /// A clock the parser's fallback (42 MHz since Core 1.10.0) cannot produce, so the spacing of
    /// the imported samples shows which rung of the resolution order won.
    /// </summary>
    private const uint OtherDeviceClockHz = 40_000_000;

    /// <summary>The ticks between two samples of the written log: 10.5 ms at 40 MHz.</summary>
    private const uint TicksPerSample = 420_000;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "daqifi-avalonia-tests", "sd-import-ts-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    private static readonly DateTime SampleBase = new(2026, 9, 8, 14, 12, 36, DateTimeKind.Utc);

    private readonly RecordingAppLogger _log = new();

    public SdCardImportTimestampFrequencyTests()
    {
        Directory.CreateDirectory(_directory);
        DatabaseMigrator.ApplyMigrations(Factory(), DatabasePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ---- The device's clock reaches Core's parser ----------------------------------------------

    /// <summary>
    /// The defect. A connected device that reports a clock must pass it to the parser, or the
    /// middle rung of the resolution order is empty and a log that states no frequency of its own
    /// silently falls through to the guess.
    /// </summary>
    [Fact]
    public async Task A_log_that_states_no_clock_is_read_on_the_devices_clock()
    {
        var sessionId = await ImportFromDeviceReporting(OtherDeviceClockHz);

        var line = Assert.Single(_log.Informations, i => i.Contains("SD card session config"));
        Assert.Contains($"TimestampFreq={OtherDeviceClockHz} Hz (source: {nameof(SdCardTimestampSource.Device)})", line, StringComparison.Ordinal);

        // 420,000 ticks at 40 MHz. The 42 MHz fallback would give 10 ms.
        var times = TimesOf(sessionId);
        Assert.Equal(TimeSpan.FromMilliseconds(10.5).Ticks, times[1] - times[0]);
        Assert.DoesNotContain(_log.Warnings, MentionsFrequency);
    }

    /// <summary>
    /// A device that reports no clock must not invent one. Zero is the honest value: it leaves the
    /// rung empty so Core's own fallback governs, exactly as it would with no device at all, and
    /// the import says that the timestamps rest on a guess.
    /// </summary>
    [Fact]
    public async Task A_device_reporting_no_clock_leaves_the_parser_on_its_fallback()
    {
        await ImportFromDeviceReporting(0);

        var line = Assert.Single(_log.Informations, i => i.Contains("SD card session config"));
        Assert.Contains($"(source: {nameof(SdCardTimestampSource.Fallback)})", line, StringComparison.Ordinal);
        Assert.Single(_log.Warnings, MentionsFrequency);
    }

    // ---- The warning -------------------------------------------------------------------------

    /// <summary>
    /// A guessed frequency is the one outcome worth warning about, and the warning should say what
    /// was actually used rather than asserting the file lacked something.
    /// </summary>
    [Fact]
    public async Task A_guessed_frequency_warns_and_names_the_value_used()
    {
        await Import(SdCardTimestampSource.Fallback, 50_000_000);

        var warning = Assert.Single(_log.Warnings, MentionsFrequency);
        Assert.Contains("50,000,000", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The regression. A frequency supplied by the connected device is a correct, expected outcome
    /// — the NORMAL one for a pre-3.8.0 log parsed straight after download — and must not warn.
    /// Before the fix this warned every time, because it read our own zeroed override instead of
    /// the resolved source.
    /// </summary>
    [Fact]
    public async Task A_device_supplied_frequency_does_not_warn()
    {
        await Import(SdCardTimestampSource.Device, DeviceClockHz);

        Assert.DoesNotContain(_log.Warnings, MentionsFrequency);
    }

    /// <summary>A frequency the file itself stated is the most trustworthy source of all.</summary>
    [Fact]
    public async Task A_file_supplied_frequency_does_not_warn()
    {
        await Import(SdCardTimestampSource.LogFile, DeviceClockHz);

        Assert.DoesNotContain(_log.Warnings, MentionsFrequency);
    }

    /// <summary>
    /// The diagnostic that would have placed this bug in minutes rather than a repo-hop: the
    /// config line has to report the frequency the parser SETTLED on and the rung it came from.
    /// Logging the override instead always printed 0, which reads as "the firmware omitted it".
    /// </summary>
    [Fact]
    public async Task The_config_line_reports_the_resolved_frequency_and_its_source()
    {
        await Import(SdCardTimestampSource.Device, DeviceClockHz);

        var line = Assert.Single(_log.Informations, i => i.Contains("SD card session config"));
        Assert.Contains("42000000", line, StringComparison.Ordinal);
        Assert.Contains(nameof(SdCardTimestampSource.Device), line, StringComparison.Ordinal);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private async Task Import(SdCardTimestampSource source, uint frequencyHz)
    {
        var session = new SdCardLogSession("log_20260908_141236.bin", SampleBase, null, Entries(4))
        {
            TimestampFrequency = frequencyHz,
            TimestampFrequencySource = source
        };

        var result = await Importer().ImportSessionAsync(
            session, progress: null, ct: CancellationToken.None);

        // Guards the helper, not the subject: a failed import would make every "did not warn"
        // assertion above pass for the wrong reason.
        Assert.True(result.SessionPersisted);
    }

    /// <summary>
    /// Matches ANY warning about a frequency, case-insensitively, rather than the exact wording
    /// this fix happens to use. The pre-fix message said "No TimestampFrequency found in SD card
    /// file"; a guard that looked for today's phrasing passed against it by accident and would
    /// have let a reworded spurious warning straight back in.
    /// </summary>
    private static bool MentionsFrequency(string warning) =>
        warning.Contains("frequency", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Imports a log that states no clock from a device whose configuration states
    /// <paramref name="frequencyHz"/>, and returns the session's ID.
    /// </summary>
    private async Task<int> ImportFromDeviceReporting(uint frequencyHz)
    {
        var device = new RecordingStreamingDevice
        {
            SdCardConfiguration = new SdCardDeviceConfiguration(
                AnalogPortCount: 1,
                DigitalPortCount: 0,
                TimestampFrequency: frequencyHz,
                DeviceSerialNumber: "SN-OK",
                DevicePartNumber: "Nq1",
                FirmwareRevision: "3.9.1",
                CalibrationValues: [(1.0, 0.0)],
                Resolution: 4096,
                PortRange: [10.0],
                InternalScaleM: [1.0]),
            OnDownload = (name, _) => Task.FromResult(WriteLogThatStatesNoClock(name))
        };

        var result = await Importer().ImportFromDeviceAsync(device, "log_20260908_141236.bin");

        // Guards the helper, not the subject, as in Import below.
        Assert.True(result.SessionPersisted);
        return result.Session.ID;
    }

    /// <summary>
    /// A protobuf SD log in the temp directory, where the importer requires a download to land:
    /// four samples of one analog input, <see cref="TicksPerSample"/> apart, and no clock.
    /// </summary>
    private static SdCardDownloadResult WriteLogThatStatesNoClock(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "daqifi-464-" + Guid.NewGuid().ToString("N") + ".bin");

        using (var stream = File.Create(path))
        {
            for (var i = 0; i < 4; i++)
            {
                var message = new DaqifiOutMessage { MsgTimeStamp = (uint)((i + 1) * TicksPerSample) };
                message.AnalogInData.Add(2048);
                message.WriteDelimitedTo(stream);
            }
        }

        return new SdCardDownloadResult(name, new FileInfo(path).Length, TimeSpan.Zero, path);
    }

    private List<long> TimesOf(int sessionId)
    {
        using var context = Factory().CreateDbContext();
        return context.Samples.AsNoTracking()
            .Where(s => s.LoggingSessionID == sessionId)
            .Select(s => s.TimestampTicks)
            .Distinct()
            .OrderBy(t => t)
            .ToList();
    }

    private SdCardSessionImporter Importer() =>
        new(Factory(), SdCardSessionImporter.DOWNLOAD_STALL_TIMEOUT, _log);

    private IDbContextFactory<LoggingContext> Factory() => TestDatabase.Contexts(DatabasePath);

    private static async IAsyncEnumerable<SdCardLogEntry> Entries(
        int count,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new SdCardLogEntry(SampleBase.AddMilliseconds(i), [i * 0.5], 0u, null);
            await Task.Yield();
        }
    }
}
