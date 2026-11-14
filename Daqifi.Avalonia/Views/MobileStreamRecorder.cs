using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daqifi.Avalonia.Views;

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
    /// <see cref="Stop"/>. Call it before the device starts to stream, so no sample is lost.
    /// </summary>
    /// <returns><c>null</c> when the session is open, or a sentence for the user that says why not.</returns>
    string? Start(IReadOnlyList<IChannel> channels);

    /// <summary>
    /// Closes the open session. Does nothing when no session is open.
    /// </summary>
    /// <returns>
    /// The name of the saved session, or <c>null</c> when no sample arrived and so nothing was kept.
    /// </returns>
    string? Stop();
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
/// </remarks>
internal sealed class LoggingManagerStreamRecorder : IMobileStreamRecorder
{
    private static readonly object DatabaseLoggerSync = new();

    public string? Start(IReadOnlyList<IChannel> channels)
    {
        try
        {
            var manager = LoggingManager.Instance;
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

    public string? Stop()
    {
        try
        {
            var manager = LoggingManager.Instance;
            if (!manager.Active) { return null; }

            var session = manager.Session;
            manager.Active = false;

            // LoggingManager keeps a stopped session only when a sample arrived, and lists it in
            // LoggingSessions when it does; a session with no samples is deleted.
            return session is not null && manager.LoggingSessions.Any(s => ReferenceEquals(s, session))
                ? session.Name
                : null;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "Mobile: could not stop recording the stream");
            return null;
        }
    }

    private static void EnsureDatabaseLogger(LoggingManager manager)
    {
        lock (DatabaseLoggerSync)
        {
            if (manager.Loggers.OfType<DatabaseLogger>().Any()) { return; }
            var factory = Daqifi.Desktop.App.ServiceProvider
                .GetRequiredService<IDbContextFactory<LoggingContext>>();
            manager.AddLogger(new DatabaseLogger(factory));
        }
    }
}
