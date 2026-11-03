using System.Security.Cryptography;
using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
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

    // No recording window: the test files are always "just written".
    private PreviousSessionImport Import() => new(_previous, _current, _markers) { RecordingWindow = TimeSpan.Zero };

    private async Task<ImportOutcome> RunImport(PreviousSessionImport? import = null, IProgress<double>? progress = null)
    {
        var outcome = new ImportOutcome();
        await (import ?? Import()).ImportAsync(outcome, progress);
        return outcome;
    }

    // The release WPF schema: no Status column on Sessions.
    private void CreatePreviousDatabase(bool withStatus = false)
    {
        Execute(_previous,
            "CREATE TABLE Sessions (ID INTEGER NOT NULL PRIMARY KEY, SessionStart TEXT NOT NULL, Name TEXT NOT NULL, SampleCount INTEGER NULL" +
            (withStatus ? ", Status INTEGER NOT NULL DEFAULT 0" : "") + ");" +
            "CREATE TABLE Samples (ID INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, LoggingSessionID INTEGER NOT NULL, Value REAL NOT NULL, " +
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

    private string Hash(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
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

    [Fact]
    public async Task An_interrupted_session_is_deleted_and_imported_again_in_full()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2, 3);
        // What a killed import leaves once startup has flagged it: a partial copy, ImportFailed.
        Execute(_current,
            "INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES (7, '2026-03-29 18:51:41.7774381', 'Session_1', 2);" +
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

    [Fact]
    public async Task A_session_that_grows_during_the_copy_is_left_for_later()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Recording", 1, 2);
        var grew = false;
        var progress = new SynchronousProgress(fraction =>
        {
            // The other app writes another sample while this session is being copied.
            if (fraction > 0 && !grew)
            {
                grew = true;
                Execute(_previous,
                    "INSERT INTO Samples (ID, LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
                    "VALUES (3, 1, 3.5, 30, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1);");
            }
        });

        var outcome = await RunImport(progress: progress);

        Assert.Empty(outcome.Imported);
        Assert.Equal(1, outcome.LeftForLater);
        Assert.Equal(0L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(0L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));
        Assert.True(Import().HasSessionsToImport(), "It must be offered again.");
    }

    [Fact]
    public async Task The_newest_session_of_a_file_written_moments_ago_is_left_for_later()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Finished", 1);
        AddPreviousSession(2, "2026-03-30 18:51:41.7774381", "Maybe still recording", 2);
        var import = new PreviousSessionImport(_previous, _current, _markers) { RecordingWindow = TimeSpan.FromHours(1) };

        var outcome = await RunImport(import);

        Assert.Single(outcome.Imported);
        Assert.Equal(1, outcome.LeftForLater);
        Assert.Equal("Finished", Scalar<string>(_current, "SELECT Name FROM Sessions"));
        Assert.True(import.HasSessionsToImport(), "The newest session must be offered again.");
    }

    [Fact]
    public async Task A_matching_session_another_instance_is_still_importing_is_left_alone()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1, 2);
        Execute(_current,
            "INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES (7, '2026-03-29 18:51:41.7774381', 'Session_1', 1);" +
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
            "INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES (7, '2026-03-29 18:51:41.7774381', 'Session_1', 2);" +
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
            "INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES (1, '2026-03-29 18:51:41.7774381', 'Session_1', 2);");
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
            "INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES (7, '2026-03-29 18:51:41.7774381', 'Session_1', 2);" +
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
        Execute(_current, "INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES (7, '2026-03-29 18:51:41.7774381', 'Session_1', 2);");

        var outcome = await RunImport();

        Assert.Equal([7], outcome.Removed);
        Assert.Single(outcome.Imported);
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
