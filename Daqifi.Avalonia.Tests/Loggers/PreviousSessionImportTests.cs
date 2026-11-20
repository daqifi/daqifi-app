using System.Security.Cryptography;
using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Importing sessions from the old machine-wide database (#353). The "old" database here is built
/// with the release WPF app's real schema (read from a real one); the current database is this
/// app's, built by its own migrations.
/// </summary>
public sealed class PreviousSessionImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daqifi-import-" + Guid.NewGuid().ToString("N"));
    private readonly string _previous;
    private readonly string _current;
    private readonly string _markers;

    public PreviousSessionImportTests()
    {
        Directory.CreateDirectory(_root);
        _previous = Path.Combine(_root, "machine wide", "DAQiFiDatabase.db");
        _current = Path.Combine(_root, "per user", "DAQiFiDatabase.db");
        _markers = Path.Combine(_root, "per user");
        Directory.CreateDirectory(Path.GetDirectoryName(_previous)!);
        Directory.CreateDirectory(_markers);
        DatabaseMigrator.ApplyMigrations(TestDatabase.Contexts(_current), _current);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PreviousSessionImport WithFreeSpace(long bytes) =>
        new(_previous, _current, _markers) { FreeSpace = _ => bytes };

    private PreviousSessionImport Import() => new(_previous, _current, _markers);

    private async Task<ImportOutcome> RunImport(PreviousSessionImport? import = null, IProgress<double>? progress = null)
    {
        var outcome = new ImportOutcome();
        await (import ?? Import()).ImportAsync(outcome, progress);
        return outcome;
    }

    // The release WPF schema: no Status column on Sessions.
    private void CreatePreviousDatabase(bool withStatus = false, bool autoincrement = true)
    {
        Execute(_previous,
            "CREATE TABLE Sessions (ID INTEGER NOT NULL PRIMARY KEY, SessionStart TEXT NOT NULL, Name TEXT NOT NULL, SampleCount INTEGER NULL" +
            (withStatus ? ", Status INTEGER NOT NULL DEFAULT 0" : "") + ");" +
            $"CREATE TABLE Samples (ID INTEGER NOT NULL PRIMARY KEY{(autoincrement ? " AUTOINCREMENT" : "")}, LoggingSessionID INTEGER NOT NULL, Value REAL NOT NULL, " +
            "TimestampTicks INTEGER NOT NULL, DeviceName TEXT NOT NULL, ChannelName TEXT NOT NULL, DeviceSerialNo TEXT NOT NULL, " +
            "Color TEXT NOT NULL, Type INTEGER NOT NULL);" +
            "CREATE INDEX IX_Samples_LoggingSessionID ON Samples (LoggingSessionID);" +
            "CREATE TABLE SessionDeviceMetadata (LoggingSessionID INTEGER NOT NULL, DeviceSerialNo TEXT NOT NULL, DeviceName TEXT NOT NULL, " +
            "SamplingFrequencyHz INTEGER NOT NULL, PRIMARY KEY (LoggingSessionID, DeviceSerialNo));");
    }

    private void AddPreviousSession(long id, string start, string name, params long[] sampleIds)
    {
        Execute(_previous, $"INSERT INTO Sessions (ID, SessionStart, Name, SampleCount) VALUES ({id}, '{start}', '{name}', {sampleIds.Length});");
        foreach (var sampleId in sampleIds)
        {
            Execute(_previous,
                "INSERT INTO Samples (ID, LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
                $"VALUES ({sampleId}, {id}, {sampleId}.5, {sampleId * 10}, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1);");
        }
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(database));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(string database, string sql)
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(database));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    // Every helper closes its unpooled connection, so the file is complete on disk.
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // A session with many samples, so its share of the old file is measurable.
    private void AddBulkPreviousSession(long id, string start, long firstSampleId, long count)
    {
        Execute(_previous,
            $"INSERT INTO Sessions (ID, SessionStart, Name, SampleCount) VALUES ({id}, '{start}', 'Bulk {id}', {count});" +
            "INSERT INTO Samples (ID, LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            $"WITH RECURSIVE c(x) AS (SELECT {firstSampleId} UNION ALL SELECT x + 1 FROM c WHERE x < {firstSampleId + count - 1}) " +
            $"SELECT x, {id}, x, x, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1 FROM c;");
    }

    [Fact]
    public async Task Imports_every_session_with_its_samples_and_device_metadata()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2, 3);
        AddPreviousSession(2, "2026-06-19 00:00:20.3617132", "Bench run", 4, 5);
        Execute(_previous, "INSERT INTO SessionDeviceMetadata VALUES (2, 'SN-TEST', 'Nq1', 1000);");

        Assert.True(Import().HasSessionsToImport());
        var imported = (await RunImport()).Imported;

        Assert.Equal(2, imported.Count);
        Assert.Equal(2L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions WHERE Status = 0"));
        Assert.Equal(5L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));
        Assert.Equal(3L, Scalar<long>(_current,
            "SELECT SampleCount FROM Sessions WHERE SessionStart = '2026-03-29 18:51:41.7774381'"));
        Assert.Equal("Bench run", Scalar<string>(_current,
            "SELECT Name FROM Sessions WHERE SessionStart = '2026-06-19 00:00:20.3617132'"));
        Assert.Equal(17.5, Scalar<double>(_current, "SELECT SUM(Value) FROM Samples"), 3);
        Assert.Equal(1000L, Scalar<long>(_current,
            $"SELECT SamplingFrequencyHz FROM SessionDeviceMetadata WHERE LoggingSessionID = {imported[1]}"));
        Assert.Equal(2L, Scalar<long>(_current,
            $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {imported[1]} AND DeviceSerialNo = 'SN-TEST' AND Type = 1"));
    }

    [Fact]
    public async Task Never_changes_the_old_database()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2);
        var before = Hash(_previous);
        var filesBefore = Directory.GetFiles(Path.GetDirectoryName(_previous)!);

        _ = Import().HasSessionsToImport();
        _ = await RunImport();

        Assert.Equal(before, Hash(_previous));
        Assert.Equal(filesBefore, Directory.GetFiles(Path.GetDirectoryName(_previous)!));
    }

    [Fact]
    public void The_old_database_is_attached_read_only()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);

        using var connection = Import().OpenWithPreviousAttached();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM old.Sessions";

        var error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(8, error.SqliteErrorCode); // SQLITE_READONLY
    }

    [Fact]
    public async Task Running_it_again_imports_nothing_more()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2);

        _ = await RunImport();
        Assert.False(Import().HasSessionsToImport());
        var second = (await RunImport()).Imported;

        Assert.Empty(second);
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(2L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));
    }

    [Fact]
    public async Task A_renamed_imported_session_is_not_imported_twice()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);
        _ = await RunImport();
        Execute(_current, "UPDATE Sessions SET Name = 'Renamed'");

        Assert.False(Import().HasSessionsToImport());
        Assert.Empty((await RunImport()).Imported);
    }

    // An SD card session's start comes from the log's file name, to the whole second (#511).
    private const string SameSecond = "2026-09-01 12:00:00";

    // A session of this user's database from the same second, with one sample: the old session's
    // first sample (AddPreviousSession, sample 1) when the serial is left alone.
    // previousFirstSampleId marks it as the import's own copy of the old session with that first
    // sample ID.
    private void AddCurrentSession(long id, SessionStatus status, string serial = "SN-TEST", long? previousFirstSampleId = null)
    {
        Execute(_current,
            "INSERT INTO Sessions (ID, SessionStart, Name, Status, PreviousFirstSampleId) " +
            $"VALUES ({id}, '{SameSecond}', 'Other card log', {(int)status}, {previousFirstSampleId?.ToString() ?? "NULL"});" +
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            $"VALUES ({id}, 1.5, 10, 'Nq1', 'AI0', '{serial}', '#FF0000', 1);");
    }

    [Fact]
    public async Task Two_different_sessions_that_started_in_the_same_second_are_both_kept()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, SameSecond, "Card log", 1, 2);
        // The usual case: two devices started together.
        AddCurrentSession(9, SessionStatus.Complete, "SN-OTHER");

        Assert.True(Import().HasSessionsToImport());
        var imported = (await RunImport()).Imported;

        Assert.Single(imported);
        Assert.Equal(2L, Scalar<long>(_current, $"SELECT COUNT(*) FROM Sessions WHERE SessionStart = '{SameSecond}' AND Status = 0"));
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = 9"));
        Assert.Equal(2L, Scalar<long>(_current, $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {imported[0]}"));
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task A_partial_import_of_a_different_session_from_the_same_second_is_not_deleted()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, SameSecond, "Card log", 1, 2);
        // Another card log's import that failed part-way, from another device.
        AddCurrentSession(9, SessionStatus.ImportFailed, "SN-OTHER");

        var outcome = await RunImport();

        Assert.Empty(outcome.Removed);
        Assert.Single(outcome.Imported);
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = 9"));
    }

    [Fact]
    public async Task A_partial_copy_is_kept_when_its_old_session_has_no_samples_left()
    {
        CreatePreviousDatabase();
        // The old session lost its samples after the copy began, so it cannot be imported again:
        // the partial copy is the only copy of that data left.
        AddPreviousSession(1, SameSecond, "Emptied since");
        AddCurrentSession(9, SessionStatus.ImportFailed, previousFirstSampleId: 1);

        var outcome = await RunImport();

        Assert.Empty(outcome.Removed);
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = 9"));
    }

    [Fact]
    public void Sessions_already_in_a_database_are_not_taken_for_copies_after_the_upgrade()
    {
        var database = Path.Combine(_root, "upgraded", "DAQiFiDatabase.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using (var context = TestDatabase.Contexts(database).CreateDbContext())
        {
            context.GetInfrastructure().GetRequiredService<IMigrator>().Migrate("20260902230000_DropChannelTable");
        }

        Execute(database, $"INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES (1, '{SameSecond}', 'Before the upgrade', 0);");
        using (var context = TestDatabase.Contexts(database).CreateDbContext())
        {
            context.Database.Migrate();
        }

        Assert.Equal(1L, Scalar<long>(database, "SELECT COUNT(*) FROM Sessions WHERE PreviousFirstSampleId IS NULL"));
    }

    // A session of this user's database that starts with exactly the old session's first sample
    // (AddPreviousSession, sample 1) and then differs: two card logs from devices with no serial
    // ("Unknown") that happened to start on the same reading.
    private void AddCurrentSessionWithTheSameFirstSample(long id, SessionStatus status)
    {
        AddCurrentSession(id, status);
        Execute(_current,
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            $"VALUES ({id}, 9.5, 20, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1);");
    }

    [Fact]
    public async Task A_different_session_with_the_same_start_and_first_sample_does_not_block_the_import()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, SameSecond, "Card log", 1, 2);
        AddCurrentSessionWithTheSameFirstSample(9, SessionStatus.Complete);

        Assert.True(Import().HasSessionsToImport());
        var imported = (await RunImport()).Imported;

        Assert.Single(imported);
        Assert.Equal(2L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = 9"));
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task A_failed_SD_import_with_the_same_start_and_first_sample_is_not_deleted()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, SameSecond, "Card log", 1, 2);
        // An SD card import that failed part-way; it keeps its samples, because the card log is
        // often cut short and cannot give them again.
        AddCurrentSessionWithTheSameFirstSample(9, SessionStatus.ImportFailed);

        var outcome = await RunImport();

        Assert.Empty(outcome.Removed);
        Assert.Single(outcome.Imported);
        Assert.Equal(2L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = 9"));
    }

    [Fact]
    public async Task A_cleanup_stopped_after_its_first_batch_is_finished_by_the_next_import()
    {
        CreatePreviousDatabase();
        var span = PreviousSessionImport.BatchSpan;
        AddBulkPreviousSession(1, SameSecond, 1, 2 * span + 10);
        var partial = await StopAnImportAfter(batches: 2);

        // Its clean-up then stopped after the first batch of samples was deleted, as a closed app
        // would leave it. This is the batch DeleteSessions commits first.
        Execute(_current,
            $"DELETE FROM Samples WHERE ID IN (SELECT ID FROM Samples WHERE LoggingSessionID = {partial} LIMIT {span})");

        var outcome = await RunImport();

        Assert.Equal([(int)partial], outcome.Removed);
        Assert.Single(outcome.Imported);
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(2 * span + 10, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));
    }

    // Stops an import after some batches, as a killed or closed app would. Returns the partial
    // copy it leaves, flagged failed.
    private async Task<long> StopAnImportAfter(int batches)
    {
        using (var stop = new CancellationTokenSource())
        {
            var reports = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Import().ImportAsync(
                new ImportOutcome(), new SynchronousProgress(_ => { if (++reports == batches + 1) stop.Cancel(); }), stop.Token));
        }

        var partial = Scalar<long>(_current, $"SELECT ID FROM Sessions WHERE Status = {(int)SessionStatus.ImportFailed}");
        Assert.Equal(batches * PreviousSessionImport.BatchSpan,
            Scalar<long>(_current, $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {partial}"));
        return partial;
    }

    [Fact]
    public async Task A_partial_copy_is_kept_when_its_old_session_was_replaced_by_another_with_the_same_ID_and_start()
    {
        CreatePreviousDatabase();
        var span = PreviousSessionImport.BatchSpan;
        AddBulkPreviousSession(1, SameSecond, 1, span + 10);
        var partial = await StopAnImportAfter(batches: 1);

        // The earlier version deletes that session, then imports a different card log from the
        // same second: it gets the same session ID (the maximum plus one) and the same start, but
        // new sample IDs (the samples table never gives an ID twice).
        Execute(_previous,
            "DELETE FROM Samples WHERE LoggingSessionID = 1; DELETE FROM Sessions WHERE ID = 1;" +
            $"INSERT INTO Sessions (ID, SessionStart, Name, SampleCount) VALUES (1, '{SameSecond}', 'Another card log', 2);" +
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            "VALUES (1, 7.5, 70, 'Nq1', 'AI0', 'Unknown', '#FF0000', 1), (1, 8.5, 80, 'Nq1', 'AI0', 'Unknown', '#FF0000', 1);");

        var outcome = await RunImport();

        Assert.Empty(outcome.Removed);
        Assert.Equal(span, Scalar<long>(_current, $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {partial}"));
        Assert.Single(outcome.Imported);
        Assert.Equal(2L, Scalar<long>(_current, $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {outcome.Imported[0]}"));
    }

    [Fact]
    public async Task A_copy_does_not_block_a_replacement_old_session_with_the_same_ID_and_start()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, SameSecond, "Card log", 1, 2);
        Assert.Single((await RunImport()).Imported);

        // As above: the old session is deleted and another card log from the same second takes its
        // ID and start, with new sample IDs.
        Execute(_previous,
            "DELETE FROM Samples WHERE LoggingSessionID = 1; DELETE FROM Sessions WHERE ID = 1;" +
            $"INSERT INTO Sessions (ID, SessionStart, Name, SampleCount) VALUES (1, '{SameSecond}', 'Another card log', 1);" +
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            "VALUES (1, 7.5, 70, 'Nq1', 'AI0', 'Unknown', '#FF0000', 1);");

        Assert.True(Import().HasSessionsToImport());
        Assert.Single((await RunImport()).Imported);
        Assert.Equal(2L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions WHERE Status = 0"));
    }

    [Fact]
    public async Task A_partial_copy_is_kept_when_the_old_database_can_give_a_sample_ID_twice()
    {
        // Without AUTOINCREMENT, SQLite gives the highest deleted sample ID again, so a recreated
        // session can have the same first sample ID: the source cannot be proved.
        CreatePreviousDatabase(autoincrement: false);
        var span = PreviousSessionImport.BatchSpan;
        AddBulkPreviousSession(1, SameSecond, 1, span + 10);
        var partial = await StopAnImportAfter(batches: 1);

        var outcome = await RunImport();

        Assert.Empty(outcome.Removed);
        Assert.Equal(span, Scalar<long>(_current, $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {partial}"));
        // The kept partial copy does not stop a complete copy, or the offer would never end.
        Assert.Single(outcome.Imported);
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task A_copy_does_not_block_a_replacement_old_session_that_reuses_its_first_sample_ID()
    {
        // Without AUTOINCREMENT, SQLite gives a new sample the highest ID in the table plus one, so
        // after a delete it gives the deleted IDs again.
        CreatePreviousDatabase(autoincrement: false);
        AddPreviousSession(1, SameSecond, "Card log", 1, 2);
        AddBulkPreviousSession(2, "2026-09-01 12:05:00", 3, PreviousSessionImport.BatchSpan + 10);

        // The import copies session 1 in full, then stops in session 2, so the offer stays open.
        // Progress report 1 is the start, 2 is session 1, 3 is the first batch of session 2.
        using (var stop = new CancellationTokenSource())
        {
            var reports = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Import().ImportAsync(
                new ImportOutcome(), new SynchronousProgress(_ => { if (++reports == 3) stop.Cancel(); }), stop.Token));
        }

        var copy = Scalar<long>(_current, $"SELECT ID FROM Sessions WHERE Status = {(int)SessionStatus.Complete}");
        Assert.Equal(1L, Scalar<long>(_current, $"SELECT PreviousFirstSampleId FROM Sessions WHERE ID = {copy}"));

        // The earlier version then deletes both sessions and imports a different card log from the
        // same second. It gets session ID 1 and the same start, and SQLite gives its first sample
        // ID 1 again.
        Execute(_previous,
            "DELETE FROM Samples; DELETE FROM Sessions;" +
            $"INSERT INTO Sessions (ID, SessionStart, Name, SampleCount) VALUES (1, '{SameSecond}', 'Another card log', 1);" +
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            "VALUES (1, 7.5, 70, 'Nq1', 'AI0', 'Unknown', '#FF0000', 1);");
        Assert.Equal(1L, Scalar<long>(_previous, "SELECT MIN(ID) FROM Samples"));

        Assert.True(Import().HasSessionsToImport());
        var outcome = await RunImport();

        Assert.Single(outcome.Imported);
        Assert.Equal(7.5, Scalar<double>(_current, $"SELECT Value FROM Samples WHERE LoggingSessionID = {outcome.Imported[0]}"));
        // The complete copy of the first card log stays.
        Assert.Equal(2L, Scalar<long>(_current, $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {copy}"));
        Assert.Equal(2L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions WHERE Status = 0"));
        // Nothing can be proved a copy here, so a finished import ends the offer itself.
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task An_interrupted_session_is_deleted_and_imported_again_in_full()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2, 3);
        // What a killed import leaves once startup has flagged it: a partial copy, ImportFailed.
        Execute(_current,
            "INSERT INTO Sessions (ID, SessionStart, Name, Status, PreviousFirstSampleId) VALUES (7, '2026-03-29 18:51:41.7774381', 'Session_1', 2, 1);" +
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            "VALUES (7, 1.5, 10, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1);");

        Assert.True(Import().HasSessionsToImport());
        var imported = (await RunImport()).Imported;

        Assert.Single(imported);
        Assert.NotEqual(7, imported[0]);
        Assert.Equal(0L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions WHERE ID = 7"));
        Assert.Equal(3L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions WHERE Status = 0"));
    }

    [Fact]
    public async Task Stopped_part_way_it_leaves_one_partial_session_and_a_rerun_completes_each_session_once()
    {
        CreatePreviousDatabase();
        var span = PreviousSessionImport.BatchSpan;
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2);
        AddPreviousSession(2, "2026-04-01 10:00:00.0000000", "Session_2", 10, span + 10, 2 * span + 10);
        AddPreviousSession(3, "2026-04-02 10:00:00.0000000", "Session_3", 3 * span);
        using var stop = new CancellationTokenSource();
        var reports = 0;

        // Stop during session 2's batches, as a killed or closed app would.
        var interrupted = new ImportOutcome();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Import().ImportAsync(
            interrupted, new SynchronousProgress(_ => { if (++reports == 3) stop.Cancel(); }), stop.Token));
        Assert.Single(interrupted.Imported);

        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions WHERE Status = 0"));
        // The partial copy is flagged failed at once, so a retry in the same run replaces it.
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions WHERE Status = 2"));
        Assert.Equal(2L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));

        Assert.True(Import().HasSessionsToImport());
        var resumed = (await RunImport()).Imported;

        Assert.Equal(2, resumed.Count);
        Assert.Equal(3L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions WHERE Status = 0"));
        Assert.Equal(3L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(6L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));
        Assert.Equal(3L, Scalar<long>(_current, "SELECT SampleCount FROM Sessions WHERE Name = 'Session_2'"));
    }

    [Fact]
    public async Task Copies_across_batch_boundaries()
    {
        CreatePreviousDatabase();
        var span = PreviousSessionImport.BatchSpan;
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, span, span + 1, 2 * span + 7);
        // Another session's sample inside the same ID range must not be pulled in.
        AddPreviousSession(2, "2026-04-01 10:00:00.0000000", "Session_2", span + 2);
        var reports = new List<double>();

        _ = await RunImport(progress: new SynchronousProgress(reports.Add));

        Assert.Equal(4L, Scalar<long>(_current,
            "SELECT COUNT(*) FROM Samples s JOIN Sessions m ON m.ID = s.LoggingSessionID WHERE m.Name = 'Session_1'"));
        Assert.Equal(5L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));
        Assert.Equal(1.0, reports[^1]);
        Assert.True(reports.Count > 3, "Progress should be reported per batch.");
    }

    [Fact]
    public async Task Skips_sessions_without_samples_and_unfinished_ones()
    {
        CreatePreviousDatabase(withStatus: true);
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Empty");
        AddPreviousSession(2, "2026-03-30 18:51:41.7774381", "Half imported from a card", 1);
        Execute(_previous, "UPDATE Sessions SET Status = 2 WHERE ID = 2");

        Assert.False(Import().HasSessionsToImport());
        Assert.Empty((await RunImport()).Imported);
    }

    [Fact]
    public void Is_not_offered_again_once_marked_done()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);

        Import().MarkDone();

        Assert.False(Import().HasSessionsToImport());
        Assert.True(File.Exists(Path.Combine(_markers, PreviousSessionImport.MarkerFileName)));
    }

    [Fact]
    public void Is_not_offered_without_an_old_database()
    {
        Assert.False(Import().HasSessionsToImport());
        Assert.False(new PreviousSessionImport(null, _current, _markers).HasSessionsToImport());
    }

    [Fact]
    public void Is_not_offered_when_the_old_file_is_not_a_DAQiFi_database()
    {
        File.WriteAllText(_previous, "not a database");

        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task A_check_that_fails_is_not_mistaken_for_nothing_left()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);
        await RunImport();
        Assert.False(Import().CheckForSessionsToImport());

        // Unreadable now: the answer is "can't tell", which must not retire the offer.
        File.WriteAllText(_previous, "not a database");
        Assert.Null(Import().CheckForSessionsToImport());
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task A_lock_held_elsewhere_fails_the_import_instead_of_hanging_it()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);
        using var holder = new SqliteConnection(TestDatabase.ConnectionString(_previous));
        holder.Open();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();
        }

        var import = new PreviousSessionImport(_previous, _current, _markers) { LockTimeoutSeconds = 1 };
        var run = Task.Run(() => import.ImportAsync(new ImportOutcome()));

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(run, finished);
        await Assert.ThrowsAsync<SqliteException>(() => run);
    }

    [Fact]
    public async Task Waits_out_a_short_write_lock_on_this_users_database()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2);
        using var writer = new SqliteConnection(TestDatabase.ConnectionString(_current));
        writer.Open();
        var transaction = writer.BeginTransaction(deferred: false);
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            transaction.Commit();
        });

        var import = new PreviousSessionImport(_previous, _current, _markers) { LockTimeoutSeconds = 20 };
        var outcome = new ImportOutcome();
        await Task.Run(() => import.ImportAsync(outcome));
        await release;

        Assert.Single(outcome.Imported);
    }

    [Fact]
    public void Never_creates_the_current_database()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);
        var missing = Path.Combine(_root, "nowhere", "DAQiFiDatabase.db");

        Assert.False(new PreviousSessionImport(_previous, missing, _markers).HasSessionsToImport());
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void Is_inert_for_this_test_run_because_the_data_folder_is_overridden()
    {
        // The test assembly sets DAQIFI_DATA_DIR (TestDataDirectory): an isolated run, which must
        // never pick up the real machine's data.
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DAQIFI_DATA_DIR")));

        Assert.False(PreviousSessionImport.ForThisPc(_current).HasSessionsToImport());
    }

    [Fact]
    public async Task New_session_IDs_never_reuse_an_ID_orphan_rows_still_carry()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);
        // Left by an old delete that ran without foreign keys: samples whose session is gone.
        Execute(_current,
            "PRAGMA foreign_keys = OFF; " +
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            "VALUES (41, 9.9, 1, 'Other', 'AI9', 'SN-OTHER', '#000000', 1);");

        var imported = (await RunImport()).Imported;

        Assert.True(imported[0] > 41, $"Got ID {imported[0]}, which orphan samples may still use.");
        Assert.Equal(1L, Scalar<long>(_current, $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {imported[0]}"));
    }

    // The earlier version writes a session's sample count only when its recording stops, so a
    // session without one has no recorded end: it may still be recording.
    private void ForgetSampleCount(long sessionId) =>
        Execute(_previous, $"UPDATE Sessions SET SampleCount = NULL WHERE ID = {sessionId};");

    private void AddPreviousSample(long sessionId, long sampleId) => Execute(_previous, SampleInsert(sessionId, sampleId));

    private static string SampleInsert(long sessionId, long sampleId) =>
        "INSERT INTO Samples (ID, LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
        $"VALUES ({sampleId}, {sessionId}, {sampleId}.5, {sampleId * 10}, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1);";

    // A sample the other app records through a connection it keeps open.
    private static void Record(SqliteConnection recorder, long sessionId, long sampleId)
    {
        using var insert = recorder.CreateCommand();
        insert.CommandText = SampleInsert(sessionId, sampleId);
        insert.ExecuteNonQuery();
    }

    // Many samples at once for an old session, with consecutive IDs.
    private void AddBulkPreviousSamples(long sessionId, long firstSampleId, long count) =>
        Execute(_previous,
            "INSERT INTO Samples (ID, LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            $"WITH RECURSIVE c(x) AS (SELECT {firstSampleId} UNION ALL SELECT x + 1 FROM c WHERE x < {firstSampleId + count - 1}) " +
            $"SELECT x, {sessionId}, x, x, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1 FROM c;");

    private long CopiedSamples(string name) => Scalar<long>(_current,
        $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = (SELECT ID FROM Sessions WHERE Name = '{name}')");

    // The other app writes the old database once, after the first batch of the copy is in.
    private static SynchronousProgress AfterTheFirstBatch(Action write) => OncePast(0, write);

    // The other app writes the old database once, when the import's progress first passes a point.
    private static SynchronousProgress OncePast(double point, Action write)
    {
        var wrote = false;
        return new SynchronousProgress(fraction =>
        {
            if (fraction > point && !wrote)
            {
                wrote = true;
                write();
            }
        });
    }

    [Fact]
    public async Task A_sample_added_during_the_copy_is_copied_by_the_next_import()
    {
        // The other app writes another sample while this session is copied. The copy holds the
        // samples there were when the import listed the session; the new one is offered again.
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Recording", 1, 2);

        var first = await RunImport(progress: AfterTheFirstBatch(() => AddPreviousSample(1, 3)));

        Assert.Single(first.Imported);
        Assert.Equal(2L, CopiedSamples("Recording"));
        Assert.True(Import().HasSessionsToImport(), "The new sample must be offered.");

        Assert.Equal(first.Imported, (await RunImport()).Imported);
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(3L, CopiedSamples("Recording"));
        Assert.Equal(3L, Scalar<long>(_current, "SELECT SampleCount FROM Sessions"));
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task An_older_session_still_recording_beside_a_newer_one_is_copied_in_full_by_the_next_import()
    {
        // Two copies of the earlier version record at once (#514), so their sample IDs interleave.
        // The newer session gets a sample during the import, the older one only after it.
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Older recording", 1, 2);
        AddPreviousSession(2, "2026-03-30 18:51:41.7774381", "Newer recording", 3, 4);
        ForgetSampleCount(1);
        ForgetSampleCount(2);

        Assert.Equal(2, (await RunImport(progress: AfterTheFirstBatch(() => AddPreviousSample(2, 5)))).Imported.Count);
        Assert.True(Import().HasSessionsToImport(), "The newer session's new sample must be offered.");

        // The older session gets its last sample, then both recordings stop and record their counts.
        AddPreviousSample(1, 6);
        Execute(_previous, "UPDATE Sessions SET SampleCount = 3;");

        Assert.Equal(2, (await RunImport()).Imported.Count);
        Assert.Equal(2L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(3L, CopiedSamples("Older recording"));
        Assert.Equal(3L, CopiedSamples("Newer recording"));
        Assert.Equal(3L, Scalar<long>(_current, "SELECT SampleCount FROM Sessions WHERE Name = 'Older recording'"));
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task A_session_counted_while_it_recorded_is_copied_in_full_by_the_next_import()
    {
        // A second copy of the earlier version that starts up writes a count for each session that
        // has none yet, also one the first copy is still recording. That count matches the samples
        // so far, so the session looks finished. The import does not read it.
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Counted while recording", 1, 2);
        AddPreviousSession(2, "2026-03-30 18:51:41.7774381", "Newer recording", 3);
        ForgetSampleCount(2);

        Assert.Equal(2, (await RunImport(progress: AfterTheFirstBatch(() => AddPreviousSample(2, 4)))).Imported.Count);

        // The older session gets its third sample, then both recordings stop and record their counts.
        AddPreviousSample(1, 5);
        Execute(_previous, "UPDATE Sessions SET SampleCount = 3 WHERE ID = 1; UPDATE Sessions SET SampleCount = 2 WHERE ID = 2;");

        Assert.Equal(2, (await RunImport()).Imported.Count);
        Assert.Equal(3L, CopiedSamples("Counted while recording"));
        Assert.Equal(2L, CopiedSamples("Newer recording"));
        Assert.False(Import().HasSessionsToImport());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_session_that_gets_a_sample_later_in_the_import_is_brought_up_to_date_by_the_next_import(bool writeAheadLog)
    {
        // The first session has no new sample during its own copy, so that copy finishes. Its next
        // sample comes while the second session is copied.
        CreatePreviousDatabase();
        if (writeAheadLog)
        {
            Execute(_previous, "PRAGMA journal_mode = WAL;");
        }

        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Copied first", 1, 2);
        AddPreviousSession(2, "2026-03-30 18:51:41.7774381", "Copied second", 3, 4, 5);

        // Two of the five sample IDs are done when the first session's copy ends; the write comes
        // after the second session's batch.
        Assert.Equal(2, (await RunImport(progress: OncePast(0.4, () => AddPreviousSample(1, 6)))).Imported.Count);
        Assert.True(Import().HasSessionsToImport(), "The first session's new sample must be offered.");

        Assert.Single((await RunImport()).Imported);
        Assert.Equal(3L, CopiedSamples("Copied first"));
        Assert.Equal(3L, CopiedSamples("Copied second"));
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task A_session_written_to_the_write_ahead_log_is_brought_up_to_date_by_the_next_import()
    {
        // The other app records in WAL mode and keeps its connection open, so its commits stay in
        // the -wal file.
        CreatePreviousDatabase();
        Execute(_previous, "PRAGMA journal_mode = WAL;");
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Recording", 1, 2);
        using var recorder = new SqliteConnection(TestDatabase.ConnectionString(_previous));
        recorder.Open();
        Record(recorder, 1, 3);

        Assert.Single((await RunImport()).Imported);
        Assert.Equal(3L, CopiedSamples("Recording"));

        Record(recorder, 1, 4);
        Assert.True(Import().HasSessionsToImport(), "The sample after the import must be offered.");
        recorder.Close();

        Assert.Single((await RunImport()).Imported);
        Assert.Equal(4L, CopiedSamples("Recording"));
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task A_sample_added_after_a_quiet_import_is_copied_by_the_next_import()
    {
        // The recorder is open but has written nothing for a long time, so the import copies its two
        // samples. Its third sample comes after the import, and then it stops. No check at import
        // time can see that sample coming.
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Quiet recording", 1, 2);
        using var recorder = new SqliteConnection(TestDatabase.ConnectionString(_previous));
        recorder.Open();
        File.SetLastWriteTimeUtc(_previous, DateTime.UtcNow.AddHours(-1));

        Assert.Single((await RunImport()).Imported);

        // As the app does after an import: nothing is left, so the offer is retired.
        Assert.False(Import().CheckForSessionsToImport());
        Import().MarkDone();

        Record(recorder, 1, 3);
        recorder.Close();

        Assert.True(Import().HasSessionsToImport(), "The new sample must be offered.");
        await RunImport();
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(3L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));
        Assert.Equal(3L, Scalar<long>(_current, "SELECT SampleCount FROM Sessions"));
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task Once_the_offer_is_retired_an_import_brings_copies_up_to_date_and_imports_no_new_session()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Imported", 1, 2);
        await RunImport();
        Import().MarkDone();
        AddPreviousSample(1, 3);
        AddPreviousSession(2, "2026-11-20 09:00:00.0000000", "Recorded after the import", 4);

        var outcome = await RunImport();

        Assert.Single(outcome.Imported);
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(3L, CopiedSamples("Imported"));
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task A_copy_brought_up_to_date_gets_the_devices_its_recording_picked_up()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Recording", 1);
        Execute(_previous, "INSERT INTO SessionDeviceMetadata VALUES (1, 'SN-TEST', 'Nq1', 1000);");
        var copy = Assert.Single((await RunImport()).Imported);

        AddPreviousSample(1, 2);
        Execute(_previous, "INSERT INTO SessionDeviceMetadata VALUES (1, 'SN-OTHER', 'Nq2', 500);");
        await RunImport();

        Assert.Equal(2L, Scalar<long>(_current, $"SELECT COUNT(*) FROM SessionDeviceMetadata WHERE LoggingSessionID = {copy}"));
    }

    [Fact]
    public async Task A_copy_deleted_during_the_import_is_not_brought_back()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Kept", 1, 2);
        AddPreviousSession(2, "2026-03-30 18:51:41.7774381", "Deleted meanwhile", 3, 4);
        await RunImport();
        AddPreviousSample(1, 5);
        AddPreviousSample(2, 6);
        var kept = Scalar<long>(_current, "SELECT ID FROM Sessions WHERE Name = 'Kept'");
        var deleted = Scalar<long>(_current, "SELECT ID FROM Sessions WHERE Name = 'Deleted meanwhile'");

        // The user deletes the second copy once the first one is brought up to date.
        var outcome = await RunImport(progress: AfterTheFirstBatch(() => Execute(_current,
            $"DELETE FROM Samples WHERE LoggingSessionID = {deleted}; DELETE FROM Sessions WHERE ID = {deleted};")));

        Assert.Equal([(int)kept], outcome.Imported);
        Assert.Equal(3L, CopiedSamples("Kept"));
        Assert.Equal(0L, Scalar<long>(_current, $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {deleted}"));
        Assert.Equal(0L, Scalar<long>(_current, $"SELECT COUNT(*) FROM SessionDeviceMetadata WHERE LoggingSessionID = {deleted}"));
    }

    [Fact]
    public async Task Bringing_a_copy_up_to_date_stopped_part_way_keeps_its_samples_and_last_sample_ID_in_step()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Long recording", 1);
        var copy = Assert.Single((await RunImport()).Imported);
        // The recording goes on for more than one batch.
        var later = PreviousSessionImport.BatchSpan + 10;
        AddBulkPreviousSamples(1, 2, later);

        using (var stop = new CancellationTokenSource())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Import().ImportAsync(
                new ImportOutcome(), new SynchronousProgress(fraction => { if (fraction > 0) stop.Cancel(); }), stop.Token));
        }

        var afterOneBatch = 1 + PreviousSessionImport.BatchSpan;
        Assert.Equal(afterOneBatch, Scalar<long>(_current, $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {copy}"));
        Assert.Equal(afterOneBatch, Scalar<long>(_current, $"SELECT SampleCount FROM Sessions WHERE ID = {copy}"));
        Assert.Equal(afterOneBatch, Scalar<long>(_current, $"SELECT PreviousLastSampleId FROM Sessions WHERE ID = {copy}"));
        Assert.Equal((long)SessionStatus.Complete, Scalar<long>(_current, $"SELECT Status FROM Sessions WHERE ID = {copy}"));

        Assert.Equal([copy], (await RunImport()).Imported);
        Assert.Equal(1 + later, Scalar<long>(_current, $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {copy}"));
        Assert.Equal(1 + later, Scalar<long>(_current, $"SELECT SampleCount FROM Sessions WHERE ID = {copy}"));
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task Needs_disk_space_only_for_the_samples_a_copy_is_behind()
    {
        CreatePreviousDatabase();
        AddBulkPreviousSession(1, "2026-03-29 18:51:41.7774381", 1, 20_000);
        await RunImport();
        // The recording goes on for as many samples again: half the old file is left to copy.
        AddBulkPreviousSamples(1, 20_001, 20_000);
        var fileLength = new FileInfo(_previous).Length;

        await Assert.ThrowsAsync<NotEnoughDiskSpaceException>(() => RunImport(WithFreeSpace(fileLength / 4)));
        Assert.Equal(20_000L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));

        Assert.Single((await RunImport(WithFreeSpace(fileLength * 3 / 4))).Imported);
        Assert.Equal(40_000L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));
    }

    [Fact]
    public async Task A_copy_with_no_last_sample_ID_is_not_compared()
    {
        // Copies made before copies recorded their last sample (no release made any) cannot be
        // compared with their old session.
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Imported before", 1, 2, 3);
        AddCurrentSession(5, SessionStatus.Complete, previousFirstSampleId: 1);
        Import().MarkDone();

        Assert.False(Import().HasSessionsToImport());
        Assert.Empty((await RunImport()).Imported);
    }

    [Fact]
    public async Task A_session_with_no_recorded_end_is_imported_when_nothing_writes_the_old_database()
    {
        // The earlier version stopped before it wrote the count (a crash, say) and has not run since.
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Never counted", 1, 2);
        ForgetSampleCount(1);

        var outcome = await RunImport();

        Assert.Single(outcome.Imported);
        Assert.Equal(2L, Scalar<long>(_current, "SELECT SampleCount FROM Sessions WHERE Status = 0"));
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task Without_AUTOINCREMENT_an_import_ends_the_offer_itself()
    {
        // No copy can be proved here (sample IDs can be given twice), so a later check would offer
        // every session again.
        CreatePreviousDatabase(autoincrement: false);
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Finished", 1);
        AddPreviousSession(2, "2026-03-30 18:51:41.7774381", "Also finished", 2);

        Assert.Equal(2, (await RunImport()).Imported.Count);
        Assert.False(Import().HasSessionsToImport());
    }

    [Fact]
    public async Task Without_AUTOINCREMENT_a_copy_never_takes_samples_from_a_session_that_reuses_its_sample_IDs()
    {
        // Here a recreated session can get its predecessor's sample IDs, so a copy's last sample ID
        // proves nothing, and the copy is not compared at all.
        CreatePreviousDatabase(autoincrement: false);
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Deleted later", 1, 2);
        var copy = Assert.Single((await RunImport()).Imported);
        Execute(_previous, "DELETE FROM Samples; DELETE FROM Sessions;");
        AddPreviousSession(2, "2026-03-30 18:51:41.7774381", "Reuses the IDs", 1, 2, 3);

        Assert.False(Import().HasSessionsToImport());
        await RunImport();
        Assert.Equal(2L, Scalar<long>(_current, $"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {copy}"));
    }

    [Fact]
    public async Task A_matching_session_another_instance_is_still_importing_is_left_alone()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2);
        Execute(_current,
            "INSERT INTO Sessions (ID, SessionStart, Name, Status, PreviousFirstSampleId) VALUES (7, '2026-03-29 18:51:41.7774381', 'Session_1', 1, 1);" +
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            "VALUES (7, 1.5, 10, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1);");

        var outcome = await RunImport();

        Assert.Empty(outcome.Removed);
        Assert.Empty(outcome.Imported);
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = 7"));
    }

    [Fact]
    public async Task The_visible_list_drops_replaced_partial_copies_and_shows_imported_sessions_in_order()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2);
        Execute(_current,
            "INSERT INTO Sessions (ID, SessionStart, Name, Status, PreviousFirstSampleId) VALUES (7, '2026-03-29 18:51:41.7774381', 'Session_1', 2, 1);" +
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            "VALUES (7, 1.5, 10, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1);" +
            "INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES (3, '2026-01-01 00:00:00.0000000', 'Native', 0);" +
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            "VALUES (3, 1.5, 10, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1);");
        var manager = new LoggingManager(TestDatabase.Contexts(_current));
        foreach (var session in manager.GetLoggingSessionsSnapshot()) manager.LoggingSessions.Add(session);
        var native = manager.LoggingSessions.Single(s => s.ID == 3);

        var outcome = await RunImport();
        manager.ApplyPersistedSessionChanges(outcome.Removed, outcome.Imported);

        Assert.Equal([3, outcome.Imported[0]], manager.LoggingSessions.Select(s => s.ID));
        Assert.Same(native, manager.LoggingSessions[0]);
        Assert.Equal(SessionStatus.Complete, manager.LoggingSessions[1].Status);
        Assert.Equal(2L, manager.LoggingSessions[1].SampleCount);
    }

    [Fact]
    public async Task Never_takes_the_ID_a_logging_session_has_just_been_promised()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);
        // The logger reads its next ID, then inserts a moment later, outside any shared transaction.
        int promised;
        using (var context = TestDatabase.Contexts(_current).CreateDbContext())
        {
            promised = SessionIdAllocator.NextSessionId(context);
        }

        var imported = (await RunImport()).Imported;
        Execute(_current, $"INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES ({promised}, '2026-11-03 00:00:00.0000000', 'Live', 0);");

        Assert.NotEqual(promised, imported[0]);
    }

    [Fact]
    public async Task Never_takes_a_promised_ID_even_after_deleting_the_highest_session()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);
        // The highest session is a failed copy the import will delete, lowering the maximum.
        Execute(_current,
            "INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES (0, '2026-01-01 00:00:00.0000000', 'Native', 0);" +
            "INSERT INTO Sessions (ID, SessionStart, Name, Status, PreviousFirstSampleId) VALUES (1, '2026-03-29 18:51:41.7774381', 'Session_1', 2, 1);");
        int promised;
        using (var context = TestDatabase.Contexts(_current).CreateDbContext())
        {
            promised = SessionIdAllocator.NextSessionId(context);
        }

        var imported = (await RunImport()).Imported;
        Execute(_current, $"INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES ({promised}, '2026-11-03 00:00:00.0000000', 'Live', 0);");

        Assert.NotEqual(promised, imported[0]);
    }

    [Fact]
    public async Task Deletes_a_partial_copy_larger_than_one_batch_completely()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);
        var rows = PreviousSessionImport.BatchSpan + 10;
        Execute(_current,
            "INSERT INTO Sessions (ID, SessionStart, Name, Status, PreviousFirstSampleId) VALUES (7, '2026-03-29 18:51:41.7774381', 'Session_1', 2, 1);" +
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            $"WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < {rows}) " +
            "SELECT 7, x, x, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1 FROM c;");

        var outcome = await RunImport();

        Assert.Equal([7], outcome.Removed);
        Assert.Equal(0L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = 7"));
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));
    }

    [Fact]
    public async Task Reports_the_partial_copies_it_replaced()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);
        Execute(_current, "INSERT INTO Sessions (ID, SessionStart, Name, Status, PreviousFirstSampleId) VALUES (7, '2026-03-29 18:51:41.7774381', 'Session_1', 2, 1);");

        var outcome = await RunImport();

        Assert.Equal([7], outcome.Removed);
        Assert.Single(outcome.Imported);
    }

    [Fact]
    public async Task Needs_disk_space_only_for_the_sessions_it_copies()
    {
        CreatePreviousDatabase();
        AddBulkPreviousSession(1, "2026-03-29 18:51:41.7774381", 1, 20_000);
        await RunImport();
        AddBulkPreviousSession(2, "2026-06-19 00:00:20.3617132", 20_001, 20_000);
        var fileLength = new FileInfo(_previous).Length;

        // Half the old file is left to copy, so room for three quarters of it is plenty.
        var outcome = await RunImport(WithFreeSpace(fileLength * 3 / 4));

        Assert.Single(outcome.Imported);
        Assert.Equal(2L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions WHERE Status = 0"));
    }

    [Fact]
    public async Task Refuses_when_the_sessions_to_copy_do_not_fit()
    {
        CreatePreviousDatabase();
        AddBulkPreviousSession(1, "2026-03-29 18:51:41.7774381", 1, 20_000);
        await RunImport();
        AddBulkPreviousSession(2, "2026-06-19 00:00:20.3617132", 20_001, 20_000);
        var fileLength = new FileInfo(_previous).Length;

        var refused = await Assert.ThrowsAsync<NotEnoughDiskSpaceException>(
            () => RunImport(WithFreeSpace(fileLength / 4)));

        Assert.InRange(refused.BytesNeeded, fileLength * 3 / 10, fileLength * 7 / 10);
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));
    }

    [Fact]
    public async Task Needs_no_disk_space_when_nothing_is_left_to_copy()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2);
        await RunImport();

        var outcome = await RunImport(WithFreeSpace(0));

        Assert.Empty(outcome.Imported);
    }

    [Fact]
    public async Task Holds_the_import_lease_while_copying_and_releases_it_after()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2);
        var reclaimableDuringCopy = new List<bool>();

        await RunImport(progress: new SynchronousProgress(_ =>
        {
            using var reclaim = ImportLease.TryReclaim(_current);
            reclaimableDuringCopy.Add(reclaim is not null);
        }));

        Assert.NotEmpty(reclaimableDuringCopy);
        Assert.All(reclaimableDuringCopy, Assert.False);
        using var after = ImportLease.TryReclaim(_current);
        Assert.NotNull(after);
    }

    [Fact]
    public async Task A_second_import_of_earlier_sessions_does_not_run_alongside_the_first()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2);

        using (var first = ImportLease.TryHoldPreviousSessionsImport(_current))
        {
            Assert.NotNull(first);
            await Assert.ThrowsAsync<ImportAlreadyRunningException>(() => RunImport());
            Assert.Equal(0L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));
        }

        Assert.Single((await RunImport()).Imported);
    }

    [Fact]
    public void Startup_leaves_a_live_import_alone_and_flags_it_once_the_import_is_gone()
    {
        Execute(_current,
            "INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES (5, '2026-03-29 18:51:41.7774381', 'Session_1', 1);" +
            "INSERT INTO Samples (LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            "VALUES (5, 1, 1, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1);");
        var manager = new LoggingManager(TestDatabase.Contexts(_current), Path.Combine(_root, "profiles.xml"));

        using (ImportLease.HoldLive(_current))
        {
            manager.LoadPersistedLoggingSessions();
            Assert.Equal(1L, Scalar<long>(_current, "SELECT Status FROM Sessions WHERE ID = 5"));
        }

        manager.LoadPersistedLoggingSessions();
        Assert.Equal(2L, Scalar<long>(_current, "SELECT Status FROM Sessions WHERE ID = 5"));
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
