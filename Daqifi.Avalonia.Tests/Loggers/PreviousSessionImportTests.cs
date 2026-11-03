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
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PreviousSessionImport Import() => new(_previous, _current, _markers);

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
        SqliteConnection.ClearAllPools();
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
        var imported = await Import().ImportAsync();

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
        _ = await Import().ImportAsync();

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

        _ = await Import().ImportAsync();
        Assert.False(Import().HasSessionsToImport());
        var second = await Import().ImportAsync();

        Assert.Empty(second);
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(2L, Scalar<long>(_current, "SELECT COUNT(*) FROM Samples"));
    }

    [Fact]
    public async Task A_renamed_imported_session_is_not_imported_twice()
    {
        CreatePreviousDatabase();
        AddPreviousSession(1, "2026-03-29 18:51:41.7774381", "Session_1", 1);
        _ = await Import().ImportAsync();
        Execute(_current, "UPDATE Sessions SET Name = 'Renamed'");

        Assert.False(Import().HasSessionsToImport());
        Assert.Empty(await Import().ImportAsync());
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
        var imported = await Import().ImportAsync();

        Assert.Single(imported);
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Import().ImportAsync(
            new SynchronousProgress(_ => { if (++reports == 3) stop.Cancel(); }), stop.Token));

        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions WHERE Status = 0"));
        Assert.Equal(1L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions WHERE Status <> 0"));
        Assert.Equal(2L, Scalar<long>(_current, "SELECT COUNT(*) FROM Sessions"));

        // What startup does to a leftover import before the user can retry.
        Execute(_current, "UPDATE Sessions SET Status = 2 WHERE Status = 1");
        Assert.True(Import().HasSessionsToImport());
        var resumed = await Import().ImportAsync();

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

        _ = await Import().ImportAsync(new SynchronousProgress(reports.Add));

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
        Assert.Empty(await Import().ImportAsync());
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

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
