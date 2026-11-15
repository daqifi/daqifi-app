using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daqifi.Avalonia.Views;

/// <summary>
/// What a stopped recording left in the database, known only after its samples are committed.
/// </summary>
/// <param name="SessionName">The session's name, or <c>null</c> when no session row was opened.</param>
/// <param name="CommittedSamples">Samples of the session found in SQLite after the writer finished.</param>
/// <param name="Problem">
/// Why the save could not be confirmed (the writer did not finish in time, or the database could not
/// be read), or <c>null</c>.
/// </param>
internal sealed record RecordingResult(string? SessionName, long CommittedSamples, string? Problem)
{
    /// <summary>True when APP LOGS can list the session: it has samples in SQLite.</summary>
    public bool IsSaved => Problem is null && SessionName is not null && CommittedSamples > 0;
}

/// <summary>
/// Records the mobile shell's stream into an app logging session, so it shows in APP LOGS (#597).
/// </summary>
/// <remarks>
/// A seam only because the test project has no app host: <c>LoggingManager.Instance</c> throws
/// there (no <c>App.ServiceProvider</c>). The app always uses <see cref="LoggingManagerStreamRecorder"/>.
/// </remarks>
internal interface IMobileStreamRecorder
{
    /// <summary>
    /// Opens a session that records every sample of <paramref name="channels"/> until
    /// <see cref="StopAsync"/>. Call it before the device starts to stream, so no sample is lost.
    /// </summary>
    /// <returns><c>null</c> when the session is open, or a sentence for the user that says why not.</returns>
    string? Start(IReadOnlyList<IChannel> channels);

    /// <summary>
    /// Closes the open session at once (before the first await), then completes when the
    /// session's samples are committed to SQLite, or when the wait for them gives up.
    /// </summary>
    Task<RecordingResult> StopAsync();
}

/// <summary>
/// The desktop's logging path, used from the phone: <see cref="LoggingManager.Active"/> opens and
/// closes the session, and a <see cref="DatabaseLogger"/> writes the samples to the same SQLite
/// database that APP LOGS lists.
/// </summary>
/// <remarks>
/// <para>
/// On the desktop, <c>DaqifiViewModel</c> adds the <see cref="DatabaseLogger"/> at startup and the
/// Channels pane subscribes each enabled channel. The mobile shell creates neither, so before #597
/// <see cref="LoggingManager.Loggers"/> was empty on the phone and nothing ever set
/// <see cref="LoggingManager.Active"/>. This class does both of those steps, and nothing more: the
/// session row, the sample writes, the empty-session cleanup and the sample count are all
/// <see cref="LoggingManager"/>'s, exactly as on the desktop.
/// </para>
/// <para>
/// The database logger is created on the first recording, not at app start. It owns a background
/// writer thread and two UI timers, and a phone that never records must not pay for them.
/// </para>
/// <para>
/// A stop is confirmed only from SQLite (PR #609 review). The writer commits samples in the
/// background, and APP LOGS lists only a session with a committed sample. So a session that is in
/// <see cref="LoggingManager.LoggingSessions"/> but not yet committed is not "saved": the user could
/// open APP LOGS and find nothing, and a write that keeps failing would never show.
/// </para>
/// </remarks>
internal sealed class LoggingManagerStreamRecorder : IMobileStreamRecorder
{
    private static readonly object DatabaseLoggerSync = new();

    private readonly Func<LoggingManager> _manager;
    private readonly Func<IDbContextFactory<LoggingContext>> _contexts;
    private readonly Func<IDbContextFactory<LoggingContext>, DatabaseLogger> _createLogger;
    private readonly TimeSpan _commitWait;

    /// <summary>The app's recorder, over the process-wide manager and database.</summary>
    public LoggingManagerStreamRecorder()
        : this(
            static () => LoggingManager.Instance,
            static () => Daqifi.Desktop.App.ServiceProvider.GetRequiredService<IDbContextFactory<LoggingContext>>(),
            static contexts => new DatabaseLogger(contexts),
            TimeSpan.FromSeconds(15))
    {
    }

    /// <summary>
    /// Test constructor: a manager and database of the test's own, and a short commit wait.
    /// </summary>
    internal LoggingManagerStreamRecorder(
        Func<LoggingManager> manager,
        Func<IDbContextFactory<LoggingContext>> contexts,
        Func<IDbContextFactory<LoggingContext>, DatabaseLogger> createLogger,
        TimeSpan commitWait)
    {
        _manager = manager;
        _contexts = contexts;
        _createLogger = createLogger;
        _commitWait = commitWait;
    }

    public string? Start(IReadOnlyList<IChannel> channels)
    {
        try
        {
            var manager = _manager();
            EnsureDatabaseLogger(manager);

            // The phone records what it streams. SD-card mode records on the device instead and
            // opens no app session, so a mode left over from another pane must not apply here.
            if (manager.CurrentMode != LoggingMode.Stream)
            {
                manager.CurrentMode = LoggingMode.Stream;
            }

            // Replacing, not plain Subscribe: after a disconnect and a reconnect to the same board,
            // the old connection's channel instances are still subscribed under the same serial
            // number and name, and Subscribe would then skip the new ones.
            foreach (var channel in channels)
            {
                manager.SubscribeReplacingStale(channel);
            }

            manager.Active = true;
            if (manager.SessionStartFailure is not null)
            {
                manager.Active = false;
                return "Recording could not start: the app could not write to its logged-data database. "
                       + "Nothing was streamed. Free some storage space and try again.";
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "Mobile: could not start recording the stream");
            return "Recording could not start, so nothing was streamed. See the app log.";
        }
    }

    public Task<RecordingResult> StopAsync()
    {
        LoggingManager manager;
        LoggingSession? session;
        try
        {
            manager = _manager();
            if (!manager.Active) { return Task.FromResult(new RecordingResult(null, 0, null)); }

            session = manager.Session;
            // On the calling (UI) thread, exactly as the desktop toggle does: this ends the session
            // and makes LoggingManager stop handing samples to the writer.
            manager.Active = false;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "Mobile: could not stop recording the stream");
            return Task.FromResult(new RecordingResult(null, 0, "the recording could not be stopped cleanly"));
        }

        if (session is null) { return Task.FromResult(new RecordingResult(null, 0, null)); }
        var writer = manager.Loggers.OfType<DatabaseLogger>().FirstOrDefault();
        return Task.Run(() => ConfirmCommitted(session, writer));
    }

    /// <summary>
    /// Waits for the writer to commit everything it holds, then counts the session's samples in
    /// SQLite — the same rows APP LOGS reads.
    /// </summary>
    private RecordingResult ConfirmCommitted(LoggingSession session, DatabaseLogger? writer)
    {
        try
        {
            if (writer is not null)
            {
                writer.WaitForIdle(_commitWait);
                if (writer.HasUnsavedSamples)
                {
                    return new RecordingResult(session.Name, 0,
                        "the samples are not written to the database yet. The app keeps trying while it is open");
                }
            }

            using var context = _contexts().CreateDbContext();
            var committed = context.Samples.LongCount(s => s.LoggingSessionID == session.ID);
            return new RecordingResult(session.Name, committed, null);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, $"Mobile: could not confirm that recording {session.Name} was saved");
            return new RecordingResult(session.Name, 0, "the database could not be read to confirm it");
        }
    }

    private void EnsureDatabaseLogger(LoggingManager manager)
    {
        lock (DatabaseLoggerSync)
        {
            if (manager.Loggers.OfType<DatabaseLogger>().Any()) { return; }
            manager.AddLogger(_createLogger(_contexts()));
        }
    }
}
