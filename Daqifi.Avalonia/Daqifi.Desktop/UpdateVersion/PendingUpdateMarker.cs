using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Daqifi.Desktop.UpdateVersion;

/// <summary>
/// A note, which every user of the PC can read, that an update for one installation is about to be
/// installed (#509). A copy of DAQiFi that another user starts from that installation while the note
/// counts must not start, because the update would close it.
/// </summary>
/// <remarks>
/// <para>
/// On an install for all users, DAQiFi exits and then the updater shows its administrator prompt.
/// When the prompt is approved, the updater closes every process under the install folder, in every
/// user's session. The last check for other copies comes before the prompt. So a copy that another
/// user starts while the prompt waits was closed with no warning, and its recording or firmware
/// update was cut off. This note closes that gap.
/// </para>
/// <para>
/// Each user who writes a note has a file of their own. On Windows, a user can read a file that
/// another user made in <c>%ProgramData%</c>, but cannot replace or delete it.
/// </para>
/// <para>
/// A note stops counting when the updater restarts the user who wrote it (it does this when it
/// finishes, whether the update was installed, refused at the prompt, or failed), when the new
/// version runs, or after <see cref="Lifetime"/>, in case that restart never comes.
/// </para>
/// <para>
/// Best effort: a note that cannot be written or read only loses this protection. It never stops an
/// install or a start.
/// </para>
/// </remarks>
public sealed class PendingUpdateMarker
{
    /// <summary>How long a note counts when the updater does not restart the user who wrote it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private const string PREFIX = "pending-";

    private readonly string _directory;
    private readonly string _installKey;
    private readonly string _userKey;
    private readonly Func<DateTimeOffset> _utcNow;

    /// <param name="directory">Where the notes are kept: a folder that every user of the PC can read and add files to.</param>
    /// <param name="installRoot">The install folder, the one the updater clears of processes.</param>
    /// <param name="user">The user this process runs as, for example <c>DOMAIN\name</c>.</param>
    /// <param name="utcNow">The clock; the system clock unless a test supplies one.</param>
    public PendingUpdateMarker(string directory, string installRoot, string user, Func<DateTimeOffset>? utcNow = null)
    {
        _directory = directory;
        _installKey = Key(NormalizedRoot(installRoot));
        _userKey = Key(user.ToUpperInvariant());
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Hands over to the updater with <paramref name="apply"/>, which exits the app. On an install for
    /// all users, writes this user's note first. A copy that started before the note was written
    /// cannot see it, so after the note is written, <paramref name="isInstallInUseElsewhere"/> looks
    /// one more time for other copies. If it finds one, nothing is installed. If the install does not
    /// go ahead, the note is removed.
    /// </summary>
    /// <param name="version">The version to install.</param>
    /// <param name="needsElevation">True for an install for all users, whose updater asks for administrator permission.</param>
    /// <param name="isInstallInUseElsewhere">True when another process runs from this installation.</param>
    /// <param name="apply">Starts the updater and exits. Returns only if it could not start it, by throwing.</param>
    /// <exception cref="InvalidOperationException">Another copy of DAQiFi started from this installation.</exception>
    public void Apply(string version, bool needsElevation, Func<bool> isInstallInUseElsewhere, Action apply)
    {
        // An install for one user has no prompt: the updater installs as soon as DAQiFi exits, and it
        // can close only that user's processes.
        if (!needsElevation)
        {
            apply();
            return;
        }

        Write(version);
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
            Clear();
            throw;
        }
    }

    /// <summary>
    /// For a copy that starts: the version that another user installs to this installation now, or
    /// <c>null</c> when this copy can start. Also removes the notes that no longer count, where this
    /// user can.
    /// </summary>
    /// <param name="runningVersion">The version of this copy. A note for this version means that the update is done.</param>
    /// <param name="restartedByUpdater">
    /// True when the updater started this copy because it finished an install, whether the install
    /// worked or not. This user's own note then stops counting.
    /// </param>
    public string? VersionBeingInstalledByAnotherUser(string runningVersion, bool restartedByUpdater)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, $"{PREFIX}{_installKey}-*");
        }
        catch (Exception)
        {
            return null; // no folder yet: no update was ever left waiting on this PC
        }

        string? installing = null;
        foreach (var file in files)
        {
            var mine = string.Equals(Path.GetFileName(file), OwnName(), StringComparison.OrdinalIgnoreCase);
            var note = Read(file);
            var over = note is not { } n
                       || (_utcNow() - n.WrittenAt).Duration() >= Lifetime
                       || string.Equals(n.Version, runningVersion, StringComparison.OrdinalIgnoreCase);

            if (over || (mine && restartedByUpdater))
            {
                TryDelete(file);
                continue;
            }

            // This user's own note does not stop this user: they asked for the update, and with the
            // usual secure desktop they cannot start a program while the prompt is open.
            if (!mine) { installing ??= note!.Value.Version; }
        }

        return installing;
    }

    /// <summary>Records that <paramref name="version"/> is about to be installed by this user.</summary>
    private void Write(string version)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(OwnPath(), $"{version}\n{_utcNow().ToString("O", CultureInfo.InvariantCulture)}");
        }
        catch (Exception)
        {
            // See the remarks.
        }
    }

    private void Clear() => TryDelete(OwnPath());

    private string OwnName() => $"{PREFIX}{_installKey}-{_userKey}";

    private string OwnPath() => Path.Combine(_directory, OwnName());

    private static (string Version, DateTimeOffset WrittenAt)? Read(string file)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            var version = lines.ElementAtOrDefault(0)?.Trim();
            return !string.IsNullOrEmpty(version)
                   && DateTimeOffset.TryParse(lines.ElementAtOrDefault(1)?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var writtenAt)
                ? (version, writtenAt)
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
            // Another user's note: only its writer, or the time limit, ends it.
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
