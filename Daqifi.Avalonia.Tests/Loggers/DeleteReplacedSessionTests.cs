using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
/// <see cref="LoggingSession.SessionStart"/>, which is written once, when a session is created. The
/// controls below matter as much as the refusal: a check that refused a session the list loaded
/// honestly would make every delete fail.
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
    /// Control: the import of earlier sessions copies the old start as text, which may have no
    /// fraction. The list reads it through EF; the check must read the same value.
    /// </summary>
    [Fact]
    public void ARecordingWithAStartStoredAsPlainText_IsDeleted()
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
