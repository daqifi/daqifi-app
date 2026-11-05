using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// #519: start, then stop before any sample arrives. The stop deletes that session's rows, so the
/// next start gets the same ID back. <see cref="LoggingSession.Equals"/> compares IDs, and the
/// <c>Session</c> property skips a write of an equal value, so the next start kept the old object.
/// Saving it re-attached the old run's device metadata. With a device connected, that collided
/// with the new run's metadata, so every start failed until the app was restarted.
/// </summary>
public class QuickRestartSessionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "quickrestart-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public QuickRestartSessionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private LoggingManager NewManager()
    {
        var factory = new Factory(DatabasePath);
        DatabaseMigrator.ApplyMigrations(factory, DatabasePath);
        return new LoggingManager(factory) { CurrentMode = LoggingMode.Stream };
    }

    [Fact]
    public void RestartAfterAnEmptyRun_GetsAFreshSession_WithTheSameId()
    {
        var manager = NewManager();

        manager.Active = true;
        var first = manager.Session;
        manager.Active = false; // no samples arrived: the session is deleted

        manager.Active = true;

        Assert.Null(manager.SessionStartFailure);
        Assert.Equal(first.ID, manager.Session.ID);
        Assert.NotSame(first, manager.Session);
    }

    /// <summary>
    /// The crash path: the old object carries the old run's metadata (EF adds it to the session's
    /// collection when the metadata row is saved). The new session must not save it again.
    /// </summary>
    [Fact]
    public void RestartAfterAnEmptyRun_DoesNotCarryTheOldRunsMetadata()
    {
        var manager = NewManager();

        manager.Active = true;
        var id = manager.Session.ID;
        manager.Session.DeviceMetadata.Add(new SessionDeviceMetadata
        {
            LoggingSessionID = id,
            DeviceSerialNo = "SN-OLD",
            DeviceName = "Nq1",
            SamplingFrequencyHz = 100,
        });
        manager.Active = false;

        manager.Active = true;

        Assert.Null(manager.SessionStartFailure);
        using var context = new Factory(DatabasePath).CreateDbContext();
        Assert.Empty(context.SessionDeviceMetadata.Where(m => m.LoggingSessionID == id).ToList());
        Assert.Single(context.Sessions.Where(s => s.ID == id).ToList());
    }

    [Fact]
    public void ManyQuickToggles_KeepStarting()
    {
        var manager = NewManager();

        for (var i = 0; i < 5; i++)
        {
            manager.Active = true;
            Assert.Null(manager.SessionStartFailure);
            manager.Active = false;
        }
    }

    private sealed class Factory(string databasePath) : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() => new(TestDatabase.Options(databasePath));
    }
}
