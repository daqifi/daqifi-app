using System.Text;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// #680 review (P1): a delete confirmation stays open while another writer, such as a second DAQiFi
/// instance, deletes that recording and stores a new one at the same ID (IDs are reused). Accepting
/// the old confirmation must not delete the new recording or its samples.
/// </summary>
/// <remarks>
/// <see cref="SessionDataRepository.DeleteSession"/> deletes by ID, so it now first checks, inside its
/// transaction, that the stored row is still the recording it was given: the same
/// <see cref="LoggingSession.RecordingId"/>, a GUID given once, when the row is created. The start is
/// not enough (second review: two boards' files with the same name get the same start). The controls
/// below matter as much as the refusals: a check that refused a session the list loaded honestly would
/// make every delete fail.
/// </remarks>
public sealed class DeleteReplacedSessionTests : IDisposable
{
    private static readonly DateTime OldStart = new(2026, 11, 20, 10, 47, 12, 345, DateTimeKind.Local);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "delete-replaced-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public DeleteReplacedSessionTests()
    {
        Directory.CreateDirectory(_directory);
        DatabaseMigrator.ApplyMigrations(TestDatabase.Contexts(DatabasePath), DatabasePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>The reviewer's case: the recording at ID 2 is replaced while the confirmation is open.</summary>
    [Fact]
    public void ARecordingStoredAtTheSameIdWhileTheConfirmationIsOpen_SurvivesTheDelete()
    {
        Store(new LoggingSession(2, "SD Import - new_gamma") { SessionStart = OldStart }, samples: 20);
        var confirmed = LoadListed(2);

        // The other writer: deletes the recording, then stores a new one, which gets the same ID.
        RemoveStoredRows(2);
        Store(new LoggingSession(2, "SD Import - independent_writer") { SessionStart = OldStart.AddMinutes(5) }, samples: 50);

        var logger = new RecordingAppLogger();
        Assert.Throws<SessionReplacedException>(() => new SessionDataRepository(TestDatabase.Contexts(DatabasePath), logger).DeleteSession(confirmed));

        Assert.Equal("SD Import - independent_writer", StoredName(2));
        Assert.Equal(50, StoredSampleCount(2));
        Assert.Equal(1, StoredMetadataCount(2));
        Assert.Empty(logger.Errors);
    }

    /// <summary>
    /// The second review's case, through the app's own SD importer: two boards each log a different
    /// file named <c>log_20261111_120000.csv</c>, so the importer gives both the same start. The first
    /// is deleted, and the second gets its ID. A stale delete of the first must delete nothing.
    /// </summary>
    [Fact]
    public async Task TwoBoardsFilesWithTheSameNameAndStart_AtTheSameId_TheStaleDeleteDeletesNothing()
    {
        var first = await ImportCsv("board-4104", "7E2815914104", samples: 3);
        var listed = LoadListed(first.ID);
        Repository().DeleteSession(listed);
        var second = await ImportCsv("board-9107", "7E2815919107", samples: 2);

        Assert.Equal(first.ID, second.ID);
        Assert.Equal(first.SessionStart, second.SessionStart);

        Assert.Throws<SessionReplacedException>(() => Repository().DeleteSession(listed));

        Assert.Equal(2, StoredSampleCount(second.ID));
        Assert.Equal(["7E2815919107"], StoredSerials(second.ID));
    }

    /// <summary>A recording that another writer deleted and nothing replaced: nothing to delete.</summary>
    [Fact]
    public void ARecordingDeletedWhileTheConfirmationIsOpen_DeletesNothing()
    {
        Store(new LoggingSession(1, "Kept") { SessionStart = OldStart.AddMinutes(-5) }, samples: 10);
        Store(new LoggingSession(2, "Gone") { SessionStart = OldStart }, samples: 20);
        var confirmed = LoadListed(2);
        RemoveStoredRows(2);

        Assert.Throws<SessionReplacedException>(() => Repository().DeleteSession(confirmed));
        Assert.Equal(10, StoredSampleCount(1));
    }

    /// <summary>
    /// Control: a session the list loaded through EF, as <c>LoadPersistedLoggingSessions</c> does, is
    /// the stored recording, so the delete goes through. The other session stays.
    /// </summary>
    [Fact]
    public void TheListedRecording_IsDeleted()
    {
        Store(new LoggingSession(1, "Kept") { SessionStart = OldStart.AddMinutes(-5) }, samples: 10);
        Store(new LoggingSession(2, "Old two") { SessionStart = OldStart }, samples: 20);

        Repository().DeleteSession(LoadListed(2));

        Assert.Null(StoredName(2));
        Assert.Equal(0, StoredSampleCount(2));
        Assert.Equal(0, StoredMetadataCount(2));
        Assert.Equal(10, StoredSampleCount(1));
    }

    /// <summary>
    /// Control: the object a logging run or an SD import puts in the list is the one it saved, with
    /// <c>DateTime.Now</c> to the tick. The stored text must give back exactly that start.
    /// </summary>
    [Fact]
    public void TheObjectARunSaved_IsDeleted()
    {
        var run = new LoggingSession(3, "Session_3");
        Store(run, samples: 5);

        Repository().DeleteSession(run);

        Assert.Null(StoredName(3));
        Assert.Equal(0, StoredSampleCount(3));
    }

    /// <summary>
    /// Control: a row that something wrote without the identity column (an older build, after the
    /// migration) has no identity, and the list loads it with none. The delete cannot tell it from a
    /// replacement, so it falls back to the ID, as before the column: the row is deleted.
    /// </summary>
    [Fact]
    public void ARowWrittenWithoutTheIdentityColumn_IsDeletedById()
    {
        using (var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath)))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES (4, '2026-01-01 00:00:00', 'Copied', 0)";
            insert.ExecuteNonQuery();
        }

        Repository().DeleteSession(LoadListed(4));

        Assert.Null(StoredName(4));
    }

    /// <summary>
    /// The migration: every row in an older database gets its own identity, and a row that the list
    /// then loads from it is deleted, while the other rows stay.
    /// </summary>
    [Fact]
    public void AnOlderDatabase_GivesEachRowItsOwnIdentity_AndALoadedRowIsDeleted()
    {
        var older = Path.Combine(_directory, "older", "DAQiFiDatabase.db");
        Directory.CreateDirectory(Path.GetDirectoryName(older)!);
        using (var context = TestDatabase.Contexts(older).CreateDbContext())
        {
            context.GetService<IMigrator>().Migrate("20261120190000_AddSessionPreviousLastSampleId");
        }
        using (var connection = new SqliteConnection(TestDatabase.ConnectionString(older)))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO Sessions (ID, SessionStart, Name, Status) VALUES " +
                "(1, '2026-01-01 00:00:00', 'One', 0), (2, '2026-01-01 00:00:00', 'Two', 0), (3, '2026-01-02 00:00:00', 'Three', 0)";
            insert.ExecuteNonQuery();
        }

        DatabaseMigrator.ApplyMigrations(TestDatabase.Contexts(older), older);

        using (var context = TestDatabase.Contexts(older).CreateDbContext())
        {
            var identities = context.Sessions.AsNoTracking().OrderBy(s => s.ID).Select(s => s.RecordingId).ToList();
            Assert.Equal(3, identities.Count);
            Assert.All(identities, id => Assert.True(id is { } g && g != Guid.Empty));
            Assert.Equal(3, identities.Distinct().Count());
        }

        LoggingSession loaded;
        using (var context = TestDatabase.Contexts(older).CreateDbContext())
        {
            loaded = context.Sessions.AsNoTracking().Single(s => s.ID == 2);
        }
        new SessionDataRepository(TestDatabase.Contexts(older), new RecordingAppLogger()).DeleteSession(loaded);

        using (var context = TestDatabase.Contexts(older).CreateDbContext())
        {
            Assert.Equal([1, 3], context.Sessions.AsNoTracking().OrderBy(s => s.ID).Select(s => s.ID).ToList());
        }
    }

    /// <summary>A CSV log as a board writes it, imported through the app's own SD importer.</summary>
    private async Task<LoggingSession> ImportCsv(string folder, string serial, int samples)
    {
        var directory = Path.Combine(_directory, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "log_20261111_120000.csv");
        var csv = new StringBuilder("# Device: Nyquist 1\n" + $"# Serial Number: {serial}\n" +
                                    "# Timestamp Tick Rate: 50000000 Hz\n" + "ain0_ts,ain0_val\n");
        for (var i = 0; i < samples; i++) { csv.Append(1000 * (i + 1)).Append(',').Append(100 + i).Append('\n'); }
        File.WriteAllText(path, csv.ToString(), Encoding.ASCII);

        var result = await new SdCardSessionImporter(
            TestDatabase.Contexts(DatabasePath), SdCardSessionImporter.DOWNLOAD_STALL_TIMEOUT, new RecordingAppLogger())
            .ImportFromFileAsync(path);
        Assert.True(result.SessionPersisted);
        return result.Session;
    }

    private List<string> StoredSerials(int id)
    {
        using var context = TestDatabase.Contexts(DatabasePath).CreateDbContext();
        return context.Samples.AsNoTracking().Where(s => s.LoggingSessionID == id).Select(s => s.DeviceSerialNo).Distinct().ToList();
    }

    private SessionDataRepository Repository() => new(TestDatabase.Contexts(DatabasePath), new RecordingAppLogger());

    /// <summary>The session as the list holds it after a load: read through EF, untracked.</summary>
    private LoggingSession LoadListed(int id)
    {
        using var context = TestDatabase.Contexts(DatabasePath).CreateDbContext();
        return context.Sessions.AsNoTracking().Single(s => s.ID == id);
    }

    private void Store(LoggingSession session, int samples)
    {
        using var context = TestDatabase.Contexts(DatabasePath).CreateDbContext();
        context.Sessions.Add(session);
        context.SessionDeviceMetadata.Add(new SessionDeviceMetadata
        {
            LoggingSessionID = session.ID,
            DeviceSerialNo = "SERIAL-A",
            DeviceName = "Nq1",
            SamplingFrequencyHz = 10,
        });
        for (var i = 0; i < samples; i++)
        {
            context.Samples.Add(new DataSample
            {
                LoggingSessionID = session.ID,
                DeviceName = "Nq1",
                DeviceSerialNo = "SERIAL-A",
                ChannelName = "AI0",
                Color = "#FFD32F2F",
                Type = Daqifi.Core.Channel.ChannelType.Analog,
                Value = i,
                TimestampTicks = OldStart.Ticks + i * TimeSpan.TicksPerSecond,
            });
        }
        context.SaveChanges();
    }

    private void RemoveStoredRows(int id) =>
        Execute($"DELETE FROM Samples WHERE LoggingSessionID = {id}; " +
                $"DELETE FROM SessionDeviceMetadata WHERE LoggingSessionID = {id}; " +
                $"DELETE FROM Sessions WHERE ID = {id};");

    private string? StoredName(int id) => Scalar($"SELECT Name FROM Sessions WHERE ID = {id}") as string;

    private long StoredSampleCount(int id) => (long)Scalar($"SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = {id}")!;

    private long StoredMetadataCount(int id) => (long)Scalar($"SELECT COUNT(*) FROM SessionDeviceMetadata WHERE LoggingSessionID = {id}")!;

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
