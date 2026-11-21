using System.Reflection;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Daqifi.Avalonia.Views.Mobile;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// #680 review (P2): the mobile APP LOGS list re-reads itself when a recording is committed
/// (<see cref="LoggedSessionsMobileViewModel.RefreshSessions"/>), and that refresh does not close an
/// open delete confirm. It puts fresh objects for the same recordings in the list. A session is
/// equal only to itself, so removing the object the delete was given removed nothing: SQLite lost the
/// session and the list still showed it, for DELETE and for DELETE ALL.
/// </summary>
/// <remarks>
/// <para>The real view model, the real <see cref="SessionDataRepository"/> and a real SQLite file.
/// The list reads that file, so "the list matches SQLite" is measured, not assumed. The delete
/// commands read <c>LoggingManager.Instance.Active</c>, so the mobile host is started, and the
/// collection runs alone: other collections turn logging on.</para>
/// <para>The third test is the P1 case on this head: another writer replaces the recording while
/// the confirm is open. The replacement must survive, and the list must show it.</para>
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class MobileDeleteAfterRefreshTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DateTime Start = new(2026, 11, 20, 9, 0, 0, 125, DateTimeKind.Local);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "mobile-delete-" + Guid.NewGuid().ToString("N"));

    private readonly SerialTestUi _ui = new();

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public MobileDeleteAfterRefreshTests()
    {
        Directory.CreateDirectory(_directory);
        DatabaseMigrator.ApplyMigrations(TestDatabase.Contexts(DatabasePath), DatabasePath);
        Daqifi.Desktop.App.InitializeMobile();
    }

    public void Dispose()
    {
        _ui.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task Delete_WithARefreshWhileTheConfirmIsOpen_LeavesTheListMatchingTheDatabase()
    {
        Store(1, "Session_1", Start, samples: 10);
        Store(2, "Session_2", Start.AddMinutes(1), samples: 10);
        var appLogs = await OpenAsync();
        var target = await _ui.Get(() => appLogs.Sessions.Single(s => s.ID == 1));

        var delete = await _ui.Get(() => appLogs.DeleteSessionCommand.ExecuteAsync(target));
        await RefreshWhileTheConfirmIsOpenAsync(appLogs, target);
        await _ui.Run(() => appLogs.ConfirmOverlay.AffirmativeCommand.Execute(null));
        await delete.WaitAsync(Timeout);
        await WaitUntilIdleAsync(appLogs);

        Assert.Equal([2], StoredIds());
        Assert.Equal(StoredIds(), await ListedIdsAsync(appLogs));
        Assert.Equal("Session deleted.", await _ui.Get(() => appLogs.StatusMessage));
    }

    [Fact]
    public async Task DeleteAll_WithARefreshWhileTheConfirmIsOpen_LeavesTheListMatchingTheDatabase()
    {
        Store(1, "Session_1", Start, samples: 10);
        Store(2, "Session_2", Start.AddMinutes(1), samples: 10);
        var appLogs = await OpenAsync();
        var first = await _ui.Get(() => appLogs.Sessions[0]);

        var deleteAll = await _ui.Get(() => appLogs.DeleteAllCommand.ExecuteAsync(null));
        await RefreshWhileTheConfirmIsOpenAsync(appLogs, first);
        await _ui.Run(() => appLogs.ConfirmOverlay.AffirmativeCommand.Execute(null));
        await deleteAll.WaitAsync(Timeout);
        await WaitUntilIdleAsync(appLogs);

        Assert.Empty(StoredIds());
        Assert.Empty(await ListedIdsAsync(appLogs));
        Assert.Equal("Deleted 2 sessions.", await _ui.Get(() => appLogs.StatusMessage));
    }

    [Fact]
    public async Task Delete_WhenAnotherWriterReplacesTheRecordingWhileTheConfirmIsOpen_KeepsAndListsTheReplacement()
    {
        Store(1, "Session_1", Start, samples: 10);
        Store(2, "Session_2", Start.AddMinutes(1), samples: 10);
        var appLogs = await OpenAsync();
        var target = await _ui.Get(() => appLogs.Sessions.Single(s => s.ID == 1));

        var delete = await _ui.Get(() => appLogs.DeleteSessionCommand.ExecuteAsync(target));
        await WaitUntilAsync(() => appLogs.ConfirmOverlay.IsOpen, "the delete confirm never opened");
        Execute("DELETE FROM Samples WHERE LoggingSessionID = 1; DELETE FROM SessionDeviceMetadata WHERE LoggingSessionID = 1; DELETE FROM Sessions WHERE ID = 1;");
        Store(1, "Replacement", Start.AddMinutes(5), samples: 50);
        await _ui.Run(() => appLogs.ConfirmOverlay.AffirmativeCommand.Execute(null));
        await delete.WaitAsync(Timeout);
        await WaitUntilAsync(() => !appLogs.IsBusy && appLogs.Sessions.Any(s => s.Name == "Replacement"),
                             "the list never showed the replacement");

        Assert.Equal("Replacement", Scalar("SELECT Name FROM Sessions WHERE ID = 1"));
        Assert.Equal(50L, Scalar("SELECT COUNT(*) FROM Samples WHERE LoggingSessionID = 1"));
        Assert.Equal(StoredIds(), await ListedIdsAsync(appLogs));
        Assert.Equal("That session changed outside this list, so nothing was deleted.",
                     await _ui.Get(() => appLogs.StatusMessage));
    }

    /// <summary>
    /// The automatic refresh, between opening and accepting the confirm: a recording is committed, so
    /// the list re-reads SQLite and holds fresh objects for the same recordings.
    /// </summary>
    private async Task RefreshWhileTheConfirmIsOpenAsync(LoggedSessionsMobileViewModel appLogs, LoggingSession before)
    {
        await WaitUntilAsync(() => appLogs.ConfirmOverlay.IsOpen, "the confirm never opened");
        await _ui.Run(appLogs.RefreshSessions);
        await WaitUntilAsync(() => !appLogs.IsBusy && appLogs.Sessions.Count > 0
                                   && !appLogs.Sessions.Contains(before),
                             "the refresh never put fresh objects in the list");
        Assert.True(await _ui.Get(() => appLogs.ConfirmOverlay.IsOpen), "Arrangement: the refresh closed the confirm.");
    }

    /// <summary>The real APP LOGS view model over the test's SQLite file, with its first load finished.</summary>
    private async Task<LoggedSessionsMobileViewModel> OpenAsync()
    {
        var appLogs = await _ui.Get(() => new LoggedSessionsMobileViewModel(ReadStored, _ui.Run));
        typeof(LoggedSessionsMobileViewModel)
            .GetField("_repository", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(appLogs, new SessionDataRepository(TestDatabase.Contexts(DatabasePath), new RecordingAppLogger()));
        await WaitUntilAsync(() => appLogs.Loaded && !appLogs.IsBusy && appLogs.Sessions.Count > 0,
                             "the first APP LOGS load never finished");
        return appLogs;
    }

    private Task WaitUntilIdleAsync(LoggedSessionsMobileViewModel appLogs) =>
        WaitUntilAsync(() => !appLogs.IsBusy, "the list never went idle");

    /// <summary>Polls <paramref name="condition"/> on the UI thread until it holds.</summary>
    private async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await _ui.Get(condition))
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(10);
        }
    }

    private Task<List<int>> ListedIdsAsync(LoggedSessionsMobileViewModel appLogs) =>
        _ui.Get(() => appLogs.Sessions.Select(s => s.ID).OrderBy(id => id).ToList());

    private IReadOnlyList<LoggingSession> ReadStored()
    {
        using var context = TestDatabase.Contexts(DatabasePath).CreateDbContext();
        return context.Sessions.AsNoTracking().OrderBy(s => s.ID).ToList();
    }

    private List<int> StoredIds()
    {
        using var context = TestDatabase.Contexts(DatabasePath).CreateDbContext();
        return context.Sessions.AsNoTracking().OrderBy(s => s.ID).Select(s => s.ID).ToList();
    }

    private void Store(int id, string name, DateTime start, int samples)
    {
        using var context = TestDatabase.Contexts(DatabasePath).CreateDbContext();
        context.Sessions.Add(new LoggingSession(id, name) { SessionStart = start, SampleCount = samples });
        for (var i = 0; i < samples; i++)
        {
            context.Samples.Add(new DataSample
            {
                LoggingSessionID = id,
                DeviceName = "Nq1",
                DeviceSerialNo = "SERIAL-A",
                ChannelName = "AI0",
                Color = "#FFD32F2F",
                Type = Daqifi.Core.Channel.ChannelType.Analog,
                Value = i,
                TimestampTicks = start.Ticks + i * TimeSpan.TicksPerSecond,
            });
        }
        context.SaveChanges();
    }

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
