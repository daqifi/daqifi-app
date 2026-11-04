using System.Runtime.CompilerServices;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Issue #515: a log file the parser refuses was reported as a fault in this app.
///
/// <para>Core's parsers refuse a genuinely malformed row with an <see cref="InvalidDataException"/>
/// whose message names the row and says what is wrong with it — a sentence written for a user. The
/// import threw it away: the dialog said "verify the file is a valid SD card log file", which is
/// the one thing a user cannot check by eye, and the failure was logged at Error, which is a
/// Sentry event for a user picking a hand-edited, truncated or non-firmware file.</para>
///
/// <para>These tests pin both halves — the explanation reaches the user, and the line is a Warning
/// — and the boundary: an exception that is NOT the parser refusing the file keeps the Error path
/// and the generic dialog, because a suppressed Error costs more than a noisy one.</para>
///
/// <para><b>Why the refusal is injected rather than parsed from a file.</b> The parse-level
/// refusal arrives with Core 1.9.0's analog-value unit rule (daqifi-core#868); the Core pinned on
/// this branch, 1.8.0, has no parser arm that throws it, so no file content can produce one here.
/// The injected exception carries Core 1.9.0's own message verbatim, and it is injected from
/// inside the sample enumeration because that is where it surfaces in production: the parse is
/// lazy, so a bad row throws while the importer is writing samples, not when the session is
/// built.</para>
/// </summary>
public sealed class SdCardImportRefusedFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "sd-import-refused-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    private static readonly DateTime SampleBase = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly RecordingAppLogger _log = new();

    /// <summary>
    /// Core 1.9.0's refusal of a row that mixes the volts form with whole numbers, verbatim
    /// (<c>SdCardAnalogValueUnitRule.ResolveRowUnit</c>). A row like
    /// <c>2000,0.123,2000,100</c> states no single unit, and reading it either way would report a
    /// voltage wrong by a factor of 1000 — so this is refused on its own terms, independent of
    /// daqifi-core#883, which is about a non-finite value counting as a whole number.
    /// </summary>
    private const string CoreRefusal =
        "SD card log row '2000,0.123,2000,100' mixes values written with a decimal point with " +
        "whole numbers, so no single unit covers the row. Firmware writes one form for every " +
        "channel in a recording, so this file is not one it produced. Reading the row either way " +
        "would report a voltage wrong by a factor of 1000 or more.";

    public SdCardImportRefusedFileTests()
    {
        Directory.CreateDirectory(_directory);
        DatabaseMigrator.ApplyMigrations(Factory(), DatabasePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    #region The user is told what is wrong with the file

    [Fact]
    public void The_dialog_carries_the_parsers_own_sentence_and_what_to_do_about_it()
    {
        // This is the text the "Import Failed" dialog shows (DaqifiViewModel.ImportSdCardLogFile).
        // Both halves have to be there: the explanation is the half the app cannot write for
        // itself, and the guidance is the half the parser does not know to give.
        var failure = Assert.IsType<SdCardLogParseFailure>(
            SdCardLogParseFailureClassifier.Classify(new InvalidDataException(CoreRefusal)));

        var message = failure.BuildUserMessage();

        Assert.Contains(CoreRefusal, message, StringComparison.Ordinal);
        Assert.Contains(SdCardLogParseFailureClassifier.MALFORMED_LOG_GUIDANCE, message, StringComparison.Ordinal);

        // The sentence the user used to get instead, which told them to check the one thing they
        // cannot check.
        Assert.DoesNotContain("verify the file is a valid", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_guidance_names_neither_the_row_nor_the_file()
    {
        // Import All deduplicates guidance across the files it skips, so a card of files refused
        // for the same reason reads the sentence out once. A row or file name baked into it would
        // print one paragraph per file.
        Assert.DoesNotContain("2000", SdCardLogParseFailureClassifier.MALFORMED_LOG_GUIDANCE, StringComparison.Ordinal);
        Assert.DoesNotContain(".csv", SdCardLogParseFailureClassifier.MALFORMED_LOG_GUIDANCE, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_with_no_message_of_its_own_still_says_something()
    {
        // Nothing in Core throws such an exception — the message is the point of the type — but a
        // dialog that opens on a blank line would be worse than a plain sentence.
        var failure = Assert.IsType<SdCardLogParseFailure>(
            SdCardLogParseFailureClassifier.Classify(new InvalidDataException("   ")));

        Assert.Equal(SdCardLogParseFailureClassifier.UNSTATED_REFUSAL_EXPLANATION, failure.Explanation);
        Assert.StartsWith(SdCardLogParseFailureClassifier.UNSTATED_REFUSAL_EXPLANATION,
            failure.BuildUserMessage(), StringComparison.Ordinal);
    }

    #endregion

    #region Everything else keeps the catch-all

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(ObjectDisposedException))]
    public void A_failure_that_is_not_the_parser_refusing_the_file_is_not_classified(Type exceptionType)
    {
        // The import also talks to the database, the file system and a device. A defect in any of
        // them must keep the Error path and the generic dialog: downgrading one would hide a real
        // bug, and a suppressed Error costs more than a noisy one.
        var ex = (Exception)Activator.CreateInstance(exceptionType, "something else went wrong")!;

        Assert.Null(SdCardLogParseFailureClassifier.Classify(ex));
    }

    [Fact]
    public void An_invalid_data_exception_carried_as_an_inner_exception_is_not_classified()
    {
        // Core throws the refusal from the parse itself, so an InvalidDataException wrapped in
        // something else did not come from there. Unwrapping would classify a defect that merely
        // happens to carry one.
        Assert.Null(SdCardLogParseFailureClassifier.Classify(
            new InvalidOperationException("wrapper", new InvalidDataException(CoreRefusal))));
    }

    [Fact]
    public void Classify_rejects_a_null_exception()
    {
        Assert.Throws<ArgumentNullException>(() => SdCardLogParseFailureClassifier.Classify(null!));
    }

    #endregion

    #region A refused file does not page the team

    [Fact]
    public async Task A_refused_row_before_any_sample_is_a_warning_not_an_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Importer().ImportSessionAsync(
            Log(EntriesThenRefusal(0)), progress: null, ct: CancellationToken.None));

        // Still thrown, so the import still fails and the caller still shows a failure dialog.
        Assert.Equal(CoreRefusal, ex.Message);

        // An Error line is a Sentry event, and a file the user picked is not a fault in this app.
        Assert.Empty(_log.Errors);

        // The refusal is still recorded in full, with what the importer did about it.
        var warning = Assert.Single(_log.Warnings, w => w.Contains(CoreRefusal, StringComparison.Ordinal));
        Assert.Contains("failed before any sample was written", warning, StringComparison.Ordinal);

        // Nothing was written, so the database goes back to how it was — unchanged by this fix.
        Assert.Empty(AllSessions());
    }

    [Fact]
    public async Task A_refused_row_after_samples_keeps_them_and_still_avoids_the_error_path()
    {
        // Past the first commit, so the session keeps its prefix and is flagged as an incomplete
        // import. The level of the line is the only thing this fix changes about that.
        var clean = SdCardSessionImporter.BatchSize + 200;

        await Assert.ThrowsAsync<InvalidDataException>(() => Importer().ImportSessionAsync(
            Log(EntriesThenRefusal(clean)), progress: null, ct: CancellationToken.None));

        Assert.Empty(_log.Errors);
        var warning = Assert.Single(_log.Warnings, w => w.Contains(CoreRefusal, StringComparison.Ordinal));
        Assert.Contains("keeps them and is flagged as an incomplete import", warning, StringComparison.Ordinal);

        var session = Assert.Single(AllSessions());
        Assert.Equal(SessionStatus.ImportFailed, session.Status);
        Assert.Equal(SdCardSessionImporter.BatchSize, SampleCountFor(session.ID));
    }

    [Fact]
    public async Task A_failure_that_is_not_a_refused_file_still_logs_an_error()
    {
        // The other half of the measurement: the importer's Error path is intact for a genuine
        // defect, which is what makes the downgrade above safe to make.
        await Assert.ThrowsAsync<IOException>(() => Importer().ImportSessionAsync(
            Log(EntriesThenThrow(0, new IOException("the disk went away"))),
            progress: null, ct: CancellationToken.None));

        Assert.Single(_log.Errors);
        Assert.DoesNotContain(_log.Warnings, w => w.Contains("the disk went away", StringComparison.Ordinal));
    }

    #endregion

    #region Fixtures

    private SdCardSessionImporter Importer() =>
        new(Factory(), SdCardSessionImporter.DOWNLOAD_STALL_TIMEOUT, _log);

    /// <summary>No device configuration, so one analog channel is discovered and each entry is one row.</summary>
    private static SdCardLogSession Log(IAsyncEnumerable<SdCardLogEntry> samples) =>
        new("log_20260901_120000.csv", SampleBase, deviceConfig: null, samples);

    /// <summary>
    /// A lazy parse of <paramref name="cleanEntries"/> good rows followed by the row Core refuses
    /// — the shape of a real malformed log, where the refusal surfaces mid-write.
    /// </summary>
    private static IAsyncEnumerable<SdCardLogEntry> EntriesThenRefusal(int cleanEntries) =>
        EntriesThenThrow(cleanEntries, new InvalidDataException(CoreRefusal));

    private static async IAsyncEnumerable<SdCardLogEntry> EntriesThenThrow(
        int cleanEntries,
        Exception failure,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 0; i < cleanEntries; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new SdCardLogEntry(SampleBase.AddMilliseconds(i), [i * 0.5], 0u, null);
            await Task.Yield();
        }

        await Task.Yield();
        throw failure;
    }

    private IDbContextFactory<LoggingContext> Factory() => TestDatabase.Contexts(DatabasePath);

    private List<LoggingSession> AllSessions()
    {
        using var context = Factory().CreateDbContext();
        return context.Sessions.AsNoTracking().OrderBy(s => s.ID).ToList();
    }

    private long SampleCountFor(int sessionId)
    {
        using var context = Factory().CreateDbContext();
        return context.Samples.LongCount(s => s.LoggingSessionID == sessionId);
    }

    #endregion
}
