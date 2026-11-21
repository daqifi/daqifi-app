using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Daqifi.Desktop.UpdateVersion;

/// <summary>A running process, as a start check sees it.</summary>
/// <param name="Id">The process ID.</param>
/// <param name="Name">The process name without extension. Windows shows it for every process, to every user.</param>
/// <param name="SessionId">The Windows session the process runs in. Windows shows it for every process, to every user.</param>
/// <param name="ImagePath">
/// The full path of its executable, or <c>null</c> when this process may not read it, as for another
/// user's processes or an elevated one.
/// </param>
public sealed record SessionProcess(int Id, string Name, int SessionId, string? ImagePath = null);

/// <summary>This copy of DAQiFi: who runs it, in which session, and as which process.</summary>
/// <param name="User">The user, for example <c>DOMAIN\name</c>.</param>
/// <param name="SessionId">The Windows session this process runs in.</param>
/// <param name="ProcessId">This process.</param>
/// <param name="ProcessName">This process's name without extension.</param>
public sealed record AppCopy(string User, int SessionId, int ProcessId, string ProcessName);

/// <summary>Why a copy of DAQiFi must not start now (#509).</summary>
/// <param name="Version">The version an update of this install installs now, when that is known.</param>
/// <param name="Problem">
/// When it cannot be known: what could not be read. The start stops then too, because a note that
/// cannot be read is no evidence that the update has finished.
/// </param>
public sealed record PendingUpdate(string? Version, string? Problem)
{
    /// <summary>What the user is told.</summary>
    public string Message => Version is not null
        ? $"DAQiFi {Version} is being installed on this PC. The update closes every copy of DAQiFi, so DAQiFi cannot start now. Try again in a few minutes."
        : $"DAQiFi cannot tell if an update is being installed on this PC, so it does not start now. {Problem} Try again in a few minutes. If this message stays, an administrator can delete that file or correct the folder's permissions.";
}

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
/// copy that wrote it still runs (it starts the updater, then exits), or while this install's
/// updater runs in the writer's session. Both the updater and its elevated copy run from
/// <c>Update.exe</c> in the install folder, in that session, and the elevated copy can outlive the
/// updater, which waits for it for 10 minutes only. When a process's path can be read, it must be
/// that file. When it cannot be read (another user's process, or an elevated one), a process named
/// <see cref="UPDATER_PROCESS_NAME"/> in that session counts. That errs on the safe side.
/// </para>
/// <para>
/// A note or a notes folder that cannot be read stops the start too, and is never deleted: only a
/// note that was read and no longer counts is removed. A start looks again a few times first, in case
/// the failure was brief.
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

    /// <summary>How many more times a start looks when a note or the notes folder cannot be read.</summary>
    public const int READ_RETRIES = 4;

    /// <summary>The wait before each of those looks.</summary>
    public static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(250);

    private const string PREFIX = "pending-";

    private readonly string _directory;
    private readonly string _installKey;
    private readonly string _updaterPath;
    private readonly AppCopy _self;

    /// <param name="directory">Where the notes are kept: a folder that every user of the PC can read and add files to.</param>
    /// <param name="installRoot">The install folder, the one the updater clears of processes.</param>
    /// <param name="self">This copy of DAQiFi.</param>
    public PendingUpdateMarker(string directory, string installRoot, AppCopy self)
    {
        _directory = directory;
        var root = NormalizedPath(installRoot) ?? installRoot.ToUpperInvariant();
        _installKey = Key(root);
        _updaterPath = Path.Combine(root, $"{UPDATER_PROCESS_NAME}.exe".ToUpperInvariant());
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
    /// For a copy that starts: why it must not start now, or <c>null</c> when it can start. Also
    /// removes the notes that were read and no longer count, where this user can. Never throws.
    /// </summary>
    /// <param name="args">This copy's command line. The updater starts DAQiFi with hook arguments during an install; those runs are part of the update, so they are never stopped.</param>
    /// <param name="restartedByUpdater">True when the updater started this copy after an install, whether it worked or not.</param>
    /// <param name="processes">Every process on the PC, from all sessions.</param>
    /// <param name="wait">Waits between looks; <see cref="Thread.Sleep(TimeSpan)"/> unless a test supplies one.</param>
    public PendingUpdate? CheckAtStart(
        IReadOnlyList<string> args,
        bool restartedByUpdater,
        Func<IEnumerable<SessionProcess>> processes,
        Action<TimeSpan>? wait = null)
    {
        if (args.Any(IsInstallerHook)) { return null; }

        wait ??= Thread.Sleep;
        var waited = TimeSpan.Zero;
        var retries = 0;
        while (true)
        {
            var found = Look(processes);

            // A read can fail for a moment, for example while another copy reads the same note.
            if (found?.Problem is not null && retries < READ_RETRIES)
            {
                retries++;
                wait(ReadRetryDelay);
                continue;
            }

            if (found?.Version is null || !restartedByUpdater || waited >= RestartWait) { return found; }

            wait(RestartPoll);
            waited += RestartPoll;
        }
    }

    private PendingUpdate? Look(Func<IEnumerable<SessionProcess>> processes)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, $"{PREFIX}{_installKey}-*");
        }
        catch (DirectoryNotFoundException)
        {
            return null; // no folder yet: no update was ever handed over on this PC
        }
        catch (Exception ex)
        {
            return new PendingUpdate(null, $"The folder {_directory} could not be read ({ex.Message}).");
        }

        var readable = new List<(string File, Note Note)>();
        string? problem = null;
        foreach (var file in files)
        {
            var (note, error) = Read(file);
            if (note is not null) { readable.Add((file, note)); }
            problem ??= error;
        }

        if (readable.Count == 0) { return problem is null ? null : new PendingUpdate(null, problem); }

        // Read after the notes: the copy that wrote a note runs until the updater runs.
        List<SessionProcess> running;
        try
        {
            running = processes().ToList();
        }
        catch (Exception)
        {
            // Nothing can tell whether the update still runs. Closing someone's work is worse than
            // starting later.
            return new PendingUpdate(readable[0].Note.Version, null);
        }

        string? installing = null;
        foreach (var (file, note) in readable)
        {
            if (UpdateRuns(note, running))
            {
                installing ??= note.Version;
            }
            else
            {
                TryDelete(file); // read, and confirmed over
            }
        }

        if (installing is not null) { return new PendingUpdate(installing, null); }
        return problem is null ? null : new PendingUpdate(null, problem);
    }

    // The copy that wrote the note still runs, or this install's updater runs in its session. This
    // copy is never the one: after a restart it can have the writer's old process ID.
    private bool UpdateRuns(Note note, List<SessionProcess> running) =>
        running.Any(p => p.Id != _self.ProcessId
                         && ((p.SessionId == note.SessionId && SameName(p.Name, UPDATER_PROCESS_NAME) && IsThisInstallsUpdater(p.ImagePath))
                             || (p.Id == note.ProcessId && SameName(p.Name, note.ProcessName))));

    // A path that cannot be read (null), or not understood, could be this install's updater.
    private bool IsThisInstallsUpdater(string? imagePath) =>
        NormalizedPath(imagePath) is not { } path || path == _updaterPath;

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

    // The note, or why it could not be read. A note that is gone by the next look no longer counts.
    private static (Note? Note, string? Error) Read(string file)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception ex)
        {
            return (null, $"The note {file} could not be read ({ex.Message}).");
        }

        var version = lines.ElementAtOrDefault(0)?.Trim();
        return !string.IsNullOrEmpty(version)
               && int.TryParse(lines.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var session)
               && int.TryParse(lines.ElementAtOrDefault(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId)
            ? (new Note(version, session, processId, lines.ElementAtOrDefault(3)?.Trim() ?? ""), null)
            : (null, $"The note {file} is not in a form that DAQiFi can read.");
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

    // Windows paths are case-insensitive, and the same folder may come with or without a final
    // separator. Null for a path that is missing or cannot be understood.
    private static string? NormalizedPath(string? path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!)).ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // A file name cannot hold a path or a DOMAIN\name, so each is reduced to a fixed-length key.
    private static string Key(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
