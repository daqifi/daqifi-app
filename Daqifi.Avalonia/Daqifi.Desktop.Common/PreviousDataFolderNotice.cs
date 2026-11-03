namespace Daqifi.Desktop.Common;

/// <summary>
/// Tells the user, once, that a DAQiFi database still sits in the machine-wide
/// <c>%ProgramData%\DAQiFi</c> folder, where it is, and how to get those sessions back.
/// </summary>
/// <remarks>
/// <para>
/// Before #353 an elevated launch of this app kept its data in <c>%ProgramData%\DAQiFi</c>. It now
/// always uses the per-user folder (<see cref="AppDataPaths"/>), so a user who ran an earlier build
/// as administrator opens a different, emptier database, and their sessions seem to have vanished.
/// They have not: the files are still in the old folder. This says so.
/// </para>
/// <para>
/// The same folder is where the release DAQiFi Desktop (WPF) app keeps its database, so the text
/// covers both cases. That file is shared with another app, so this class only checks whether it
/// <b>exists</b>. It never opens, moves, migrates or deletes it.
/// </para>
/// <para>
/// Shown once per user: after the first time, a marker file in the per-user data folder records
/// that the notice was given. Nothing here may throw, because it runs during main-window startup.
/// </para>
/// </remarks>
public sealed class PreviousDataFolderNotice
{
    /// <summary>The database file name both apps use.</summary>
    internal const string DatabaseFileName = "DAQiFiDatabase.db";

    /// <summary>The marker that records the notice was shown, in the per-user data folder.</summary>
    internal const string MarkerFileName = ".previous-data-folder-notice-shown";

    private readonly string? _previousDirectory;
    private readonly string _currentDirectory;

    /// <param name="previousDirectory">
    /// The old machine-wide folder, or <c>null</c> when there is none to check (not Windows, or a
    /// <c>DAQIFI_DATA_DIR</c> override is in effect).
    /// </param>
    /// <param name="currentDirectory">The folder this app now keeps its data in.</param>
    internal PreviousDataFolderNotice(string? previousDirectory, string currentDirectory)
    {
        _previousDirectory = previousDirectory;
        _currentDirectory = currentDirectory;
    }

    /// <summary>
    /// The notice for this PC. Windows only: no other platform ever used <c>%ProgramData%</c>.
    /// Inert when <c>DAQIFI_DATA_DIR</c> is set, because that override marks an isolated test or
    /// tooling run, which must not be told about (or pick up) the real machine's data.
    /// </summary>
    public static PreviousDataFolderNotice ForThisPc()
    {
        string? previous = null;
        try
        {
            if (OperatingSystem.IsWindows()
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DAQIFI_DATA_DIR")))
            {
                var machineWide = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!string.IsNullOrEmpty(machineWide))
                {
                    previous = Path.Combine(machineWide, "DAQiFi");
                }
            }
        }
        catch
        {
            // No folder to check means no notice, never a failed startup.
        }

        return new PreviousDataFolderNotice(previous, AppDataPaths.DataDirectory);
    }

    /// <summary>
    /// The message to show, or <c>null</c> when there is nothing to say: no old database, the
    /// notice was already shown, or the old and current folders are the same place.
    /// </summary>
    /// <remarks>
    /// Records the notice as shown before returning it, so it appears once. If the marker cannot
    /// be written (an unwritable data folder, which breaks far more than this) the notice comes
    /// back at the next launch, which is the safer way to fail.
    /// </remarks>
    public string? TakeNotice()
    {
        try
        {
            if (_previousDirectory is null || SameDirectory(_previousDirectory, _currentDirectory))
            {
                return null;
            }

            var previousDatabase = Path.Combine(_previousDirectory, DatabaseFileName);
            var marker = Path.Combine(_currentDirectory, MarkerFileName);
            if (!File.Exists(previousDatabase) || File.Exists(marker))
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(_currentDirectory);
                File.WriteAllText(marker, string.Empty);
            }
            catch
            {
                // Shown again next launch; see remarks.
            }

            return Describe(_previousDirectory, _currentDirectory);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>What the notice says.</summary>
    internal static string Describe(string previousDirectory, string currentDirectory) =>
        $"DAQiFi keeps its data in your own user folder, {currentDirectory}. A DAQiFi database was also " +
        $"found in the shared folder {previousDirectory}. It holds sessions logged by the DAQiFi Desktop " +
        "app, or by an earlier version of this app run as administrator, and it has not been changed. " +
        "If DAQiFi Desktop logged them, you can still open and export them there. To see them here " +
        $"instead, close this app and copy {DatabaseFileName} from the shared folder into your own " +
        "folder. That replaces the sessions shown here, so rename the existing file first to keep it.";

    private static bool SameDirectory(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
