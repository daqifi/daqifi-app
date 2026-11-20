using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Daqifi.Desktop.UpdateVersion;

/// <summary>A running process, as any user of the PC can see it: no path, and no access to the process itself.</summary>
/// <param name="Id">The process ID.</param>
/// <param name="Name">The process name without extension.</param>
/// <param name="SessionId">The Windows session the process runs in.</param>
public sealed record SessionProcess(int Id, string Name, int SessionId);

/// <summary>This copy of DAQiFi: who runs it, in which session, and as which process.</summary>
/// <param name="User">The user, for example <c>DOMAIN\name</c>.</param>
/// <param name="SessionId">The Windows session this process runs in.</param>
/// <param name="ProcessId">This process.</param>
/// <param name="ProcessName">This process's name without extension.</param>
public sealed record AppCopy(string User, int SessionId, int ProcessId, string ProcessName);

/// <summary>
/// A note, which every user of the PC can read, that an update for one installation is being
/// installed (#509). A copy of DAQiFi that starts from that installation while the updater runs
/// must not start, because the updater would close it.
/// </summary>
/// <remarks>
/// <para>
/// On an install for all users, DAQiFi exits and then the updater shows its administrator prompt.
/// When the prompt is approved, an elevated copy of the updater closes every process under the
/// install folder, in every user's session. The last check for other copies comes before the
/// prompt. So a copy that starts while the prompt waits was closed with no warning, and its
/// recording or firmware update was cut off. This note closes that gap.
/// </para>
/// <para>
/// The note counts while the update can still close DAQiFi, and for no fixed time. Velopack 1.3.39
/// leaves no sign of that which another user can read: its lock file is in the writer's own
/// <c>%LocalAppData%</c>, and only the updater knows the ID of its elevated copy. What every user
/// can read is the list of processes, with their names and sessions. So the note counts while the
/// copy that wrote it still runs (it starts the updater, then exits), or while a process named
/// <see cref="UPDATER_PROCESS_NAME"/> runs in the writer's session. Both the updater and its
/// elevated copy have that name and run in that session, and the elevated copy can outlive the
/// updater, which waits for it for 10 minutes only. Another program named <c>Update</c> in that
/// session also keeps the note, until it ends or that user signs out. That errs on the safe side.
/// </para>
/// <para>
/// Each user has a file of their own for each session. On Windows, a user can read a file that
/// another user made in <c>%ProgramData%</c>, but cannot replace or delete it.
/// </para>
/// </remarks>
public sealed class PendingUpdateMarker
{
    /// <summary>The process name of Velopack's updater, <c>Update.exe</c>, and of its elevated copy.</summary>
    public const string UPDATER_PROCESS_NAME = "Update";

    /// <summary>
    /// How long a copy that the updater restarts waits for that updater to exit. The updater starts
    /// the copy and then exits, so for a moment it still runs when the copy looks.
    /// </summary>
    public static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(10);

    /// <summary>How often a restarted copy looks again while it waits.</summary>
    public static readonly TimeSpan RestartPoll = TimeSpan.FromMilliseconds(250);

    private const string PREFIX = "pending-";

    private readonly string _directory;
    private readonly string _installKey;
    private readonly AppCopy _self;

    /// <param name="directory">Where the notes are kept: a folder that every user of the PC can read and add files to.</param>
    /// <param name="installRoot">The install folder, the one the updater clears of processes.</param>
    /// <param name="self">This copy of DAQiFi.</param>
    public PendingUpdateMarker(string directory, string installRoot, AppCopy self)
    {
        _directory = directory;
        _installKey = Key(NormalizedRoot(installRoot));
        _self = self;
    }

    /// <summary>
    /// Hands over to the updater with <paramref name="apply"/>, which exits the app. On an install for
    /// all users, writes this copy's note first, and does not hand over without it. A copy that started
    /// before the note was written cannot see it, so after the note is written,
    /// <paramref name="isInstallInUseElsewhere"/> looks one more time for other copies. If it finds
    /// one, nothing is installed. If the install does not go ahead, the note is removed.
    /// </summary>
    /// <param name="marker">The note for this installation, or <c>null</c> when there is nowhere to write it.</param>
    /// <param name="version">The version to install.</param>
    /// <param name="needsElevation">True for an install for all users, whose updater asks for administrator permission.</param>
    /// <param name="isInstallInUseElsewhere">True when another process runs from this installation.</param>
    /// <param name="apply">Starts the updater and exits. Returns only if it could not start it, by throwing.</param>
    /// <exception cref="InvalidOperationException">The note could not be written, or another copy of DAQiFi started.</exception>
    public static void HandOver(
        PendingUpdateMarker? marker, string version, bool needsElevation, Func<bool> isInstallInUseElsewhere, Action apply)
    {
        // An install for one user has no prompt: the updater installs as soon as DAQiFi exits, and it
        // can close only that user's processes.
        if (!needsElevation)
        {
            apply();
            return;
        }

        // Without the note, a copy that another user starts during the prompt has nothing to stop it.
        if (marker is null)
        {
            throw new InvalidOperationException(
                "There is no place to tell other users that DAQiFi is being updated, so the update was not installed.");
        }

        try
        {
            marker.Write(version);
        }
        catch (Exception ex)
        {
            marker.Clear();
            throw new InvalidOperationException(
                "Other users could not be told that DAQiFi is being updated, so the update was not installed.", ex);
        }

        try
        {
            if (isInstallInUseElsewhere())
            {
                throw new InvalidOperationException(
                    "Another copy of DAQiFi started from this installation, so the update was not installed.");
            }

            apply();
        }
        catch
        {
            marker.Clear();
            throw;
        }
    }

    /// <summary>
    /// For a copy that starts: the version that an update of this installation installs now, or
    /// <c>null</c> when this copy can start. Also removes the notes that no longer count, where this
    /// user can. Never throws.
    /// </summary>
    /// <param name="args">This copy's command line. The updater starts DAQiFi with hook arguments during an install; those runs are part of the update, so they are never stopped.</param>
    /// <param name="restartedByUpdater">True when the updater started this copy after an install, whether it worked or not.</param>
    /// <param name="processes">Every process on the PC, from all sessions.</param>
    /// <param name="wait">Waits between looks while a restarted copy waits for its updater to exit; <see cref="Thread.Sleep(TimeSpan)"/> unless a test supplies one.</param>
    public string? VersionBeingInstalled(
        IReadOnlyList<string> args,
        bool restartedByUpdater,
        Func<IEnumerable<SessionProcess>> processes,
        Action<TimeSpan>? wait = null)
    {
        if (args.Any(IsInstallerHook)) { return null; }

        var waited = TimeSpan.Zero;
        while (true)
        {
            var installing = Installing(processes);
            if (installing is null || !restartedByUpdater || waited >= RestartWait) { return installing; }

            (wait ?? Thread.Sleep)(RestartPoll);
            waited += RestartPoll;
        }
    }

    private string? Installing(Func<IEnumerable<SessionProcess>> processes)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, $"{PREFIX}{_installKey}-*");
        }
        catch (Exception)
        {
            return null; // no folder yet: no update was ever handed over on this PC
        }

        var notes = files.Select(file => (File: file, Note: Read(file))).ToList();
        foreach (var unreadable in notes.Where(n => n.Note is null)) { TryDelete(unreadable.File); }
        if (notes.All(n => n.Note is null)) { return null; }

        // Read after the notes: the copy that wrote a note runs until the updater runs.
        List<SessionProcess> running;
        try
        {
            running = processes().ToList();
        }
        catch (Exception)
        {
            // A note is there and nothing can tell whether its update still runs. Closing someone's
            // work is worse than starting later.
            return notes.First(n => n.Note is not null).Note!.Version;
        }

        string? installing = null;
        foreach (var (file, note) in notes)
        {
            if (note is null) { continue; }

            if (UpdateRuns(note, running))
            {
                installing ??= note.Version;
            }
            else
            {
                TryDelete(file);
            }
        }

        return installing;
    }

    // The copy that wrote the note still runs, or an updater runs in its session. This copy is never
    // the one: after a restart it can have the writer's old process ID.
    private bool UpdateRuns(Note note, List<SessionProcess> running) =>
        running.Any(p => p.Id != _self.ProcessId
                         && ((p.SessionId == note.SessionId && SameName(p.Name, UPDATER_PROCESS_NAME))
                             || (p.Id == note.ProcessId && SameName(p.Name, note.ProcessName))));

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsInstallerHook(string arg) =>
        arg.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase)
        || arg.StartsWith("--squirrel-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Records that this copy hands <paramref name="version"/> over to the updater now.</summary>
    private void Write(string version)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllLines(OwnPath(),
        [
            version,
            _self.SessionId.ToString(CultureInfo.InvariantCulture),
            _self.ProcessId.ToString(CultureInfo.InvariantCulture),
            _self.ProcessName,
        ]);
    }

    private void Clear() => TryDelete(OwnPath());

    // One note per user and session: on Windows, a user cannot replace another user's file.
    private string OwnPath() => Path.Combine(
        _directory,
        $"{PREFIX}{_installKey}-{Key(_self.User.ToUpperInvariant())}-{_self.SessionId.ToString(CultureInfo.InvariantCulture)}");

    private sealed record Note(string Version, int SessionId, int ProcessId, string ProcessName);

    private static Note? Read(string file)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            var version = lines.ElementAtOrDefault(0)?.Trim();
            return !string.IsNullOrEmpty(version)
                   && int.TryParse(lines.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var session)
                   && int.TryParse(lines.ElementAtOrDefault(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId)
                ? new Note(version, session, processId, lines.ElementAtOrDefault(3)?.Trim() ?? "")
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
            // Another user's note: only its writer can delete it. It no longer counts either way.
        }
    }

    // Windows paths are case-insensitive, and the same folder may come with or without a final separator.
    private static string NormalizedRoot(string installRoot)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)).ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return installRoot.ToUpperInvariant();
        }
    }

    // A file name cannot hold a path or a DOMAIN\name, so each is reduced to a fixed-length key.
    private static string Key(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
