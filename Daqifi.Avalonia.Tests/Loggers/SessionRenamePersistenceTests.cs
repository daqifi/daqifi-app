using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Renaming a logging session — the write behind the editable name box on the Logged Data pane.
///
/// <para>Until now this write lived in <c>LoggedDataPanePrototype.axaml.cs</c>: a markup
/// code-behind that resolved <c>IDbContextFactory&lt;LoggingContext&gt;</c> off
/// <c>App.ServiceProvider</c>, opened a <c>DbContext</c>, found the row and saved it. It was the
/// only <c>.axaml.cs</c> in the repo that referenced EF Core or the service locator at all, and
/// nothing in this project can construct a view, so the rename was the one session write with no
/// reachable test. Moving it to <see cref="LoggingManager"/> — which already owns the sessions this
/// pane lists and the context factory they live in — is what makes these rows possible.</para>
///
/// <para>Most of these rows are characterization: the behaviour was unchanged by that move, and
/// what shows they bite is the mutation run recorded on its PR, where each is killed by exactly
/// the mutation its name claims. The blank-name rows are the exception — they are regression
/// tests for issue #427 and go red without the <see cref="string.Empty"/> normalization in
/// <see cref="LoggingManager.PersistSessionNameAsync"/>.</para>
/// </summary>
public sealed class SessionRenamePersistenceTests : IDisposable
{
    private const int SessionId = 1;

    /// <summary>The name the row is seeded with, so every assertion below moves it off a value it
    /// already held — an assertion that only ever sees the default proves nothing.</summary>
    private const string SeededName = "Session";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "session-rename-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    /// <summary>A throwaway profiles file, so nothing here can reach the developer's real one.</summary>
    private string ProfilePath => Path.Combine(_directory, "DAQifiProfilesConfiguration.xml");

    public SessionRenamePersistenceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>The headline: what the user typed is in the database afterwards.</summary>
    [Fact]
    public async Task A_typed_name_reaches_the_sessions_row()
    {
        SeedSession();

        await Manager().PersistSessionNameAsync(SessionId, "Bench run 3");

        Assert.Equal("Bench run 3", StoredName());
    }

    /// <summary>
    /// The blank case — the fix for issue #427.
    ///
    /// <para>The pane sends an empty name when the user empties the box, meaning "go back to the
    /// default label". That is stored as <c>""</c>, which <c>LoggingSession.Name</c>'s getter
    /// already renders as <c>"Session {ID}"</c> for any session that was never named, so the row
    /// reads back as the default on the next launch. A <c>null</c> arriving from any caller means
    /// the same thing and is normalized to <c>""</c> rather than written: <c>Sessions.Name</c> is
    /// <c>nullable: false</c>, and EF writes the backing field rather than that substituting
    /// getter, so a null used to throw <c>NOT NULL constraint failed: Sessions.Name</c> and leave
    /// the row holding its old name while the pane already showed the default. The code-behind
    /// logged the throw and showed the user nothing, which is why it went unnoticed.</para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Clearing_the_box_stores_a_blank_name_and_the_row_reloads_as_the_default_label(string? cleared)
    {
        SeedSession();

        await Manager().PersistSessionNameAsync(SessionId, cleared);

        Assert.Equal(string.Empty, StoredName());

        // Through a fresh context, which is what the next launch does: the stored blank renders as
        // the default label, so clearing the box is durable rather than cosmetic. Note this is not
        // the seeded name — SeededName is "Session", the default is "Session 1".
        await using var context = TestDatabase.Contexts(DatabasePath).CreateDbContext();
        Assert.Equal($"Session {SessionId}", context.Sessions.Single(session => session.ID == SessionId).Name);
    }

    /// <summary>
    /// The exception path the #427 fix must not take away with the bug. A rename that genuinely
    /// cannot be written still reaches the caller, which is the only thing that logs it — if this
    /// method started swallowing, a failed rename would be silent in both the UI and the log.
    /// </summary>
    [Fact]
    public async Task A_write_that_cannot_reach_the_database_still_surfaces_to_the_caller()
    {
        SeedSession();

        // What a half-finished "Delete All" leaves at the same path: a valid, table-less database
        // (the shape DeleteAllSessionsRecoveryTests uses for the same purpose).
        File.Delete(DatabasePath);
        File.WriteAllBytes(DatabasePath, []);

        await Assert.ThrowsAnyAsync<SqliteException>(
            () => Manager().PersistSessionNameAsync(SessionId, "Bench run 3"));
    }

    /// <summary>
    /// A debounced write can land after its session was deleted — the pane's 250 ms delay is long
    /// enough for a Delete to complete in between. That has to be a no-op rather than a throw, or
    /// the race surfaces to the user as a failed rename on a session that is already gone.
    /// </summary>
    [Fact]
    public async Task Renaming_a_session_that_is_no_longer_there_changes_nothing_and_does_not_throw()
    {
        SeedSession();

        await Manager().PersistSessionNameAsync(SessionId + 99, "Ghost");

        Assert.Equal(SeededName, StoredName());
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM Sessions"));
    }

    /// <summary>
    /// The debounce contract seen from this side: the caller cancels the previous write on every
    /// keystroke, and a cancelled write must not reach the row. Without this, a fast typist's
    /// superseded keystrokes could still be racing toward the database behind the final one.
    /// </summary>
    [Fact]
    public async Task A_cancelled_write_leaves_the_row_alone()
    {
        SeedSession();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Manager().PersistSessionNameAsync(SessionId, "Superseded", cancelled.Token));

        Assert.Equal(SeededName, StoredName());
    }

    #region Helpers

    private LoggingManager Manager() => new(TestDatabase.Contexts(DatabasePath), ProfilePath);

    private void SeedSession()
    {
        DatabaseMigrator.ApplyMigrations(TestDatabase.Contexts(DatabasePath), DatabasePath);

        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO Sessions (ID, SessionStart, Name) VALUES ($id, $start, $name)";
        insert.Parameters.AddWithValue("$id", SessionId);
        insert.Parameters.AddWithValue("$start", "2026-01-01 00:00:00");
        insert.Parameters.AddWithValue("$name", SeededName);
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// Read straight out of SQLite rather than back through EF, so a change that only ever existed
    /// in a change-tracker cannot pass this.
    /// </summary>
    private string? StoredName()
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT Name FROM Sessions WHERE ID = $id";
        query.Parameters.AddWithValue("$id", SessionId);
        return query.ExecuteScalar() as string;
    }

    private long ScalarLong(string sql)
    {
        using var connection = new SqliteConnection(TestDatabase.ConnectionString(DatabasePath));
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = sql;
        return (long)query.ExecuteScalar()!;
    }

    #endregion
}
