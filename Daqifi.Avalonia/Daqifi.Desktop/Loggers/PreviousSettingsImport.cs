// Downstream addition (#512) — no upstream counterpart, so no `// @port:` marker.

using Daqifi.Desktop.Common;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Models;

namespace Daqifi.Desktop.Logger;

/// <summary>
/// Brings saved logging profiles and the CSV delimiter over from the folder that an earlier DAQiFi
/// version used, <c>%ProgramData%\DAQiFi</c>. It runs when the user clicks Import on the offer
/// that <see cref="PreviousVersionImport"/> makes, before the sessions are copied.
/// </summary>
/// <remarks>
/// <para>
/// <b>The old files are only read.</b> The release DAQiFi Desktop (WPF) app still uses that folder.
/// The files are opened for reading only and shared for writing, so that the other app can still
/// save them during the read. A damaged file is skipped with a log line. It is never moved aside
/// or repaired, as the app does with its own files.
/// </para>
/// <para>
/// <b>Nothing that the user has is replaced.</b> A profile comes across only when no profile with
/// its ID is here (<see cref="LoggingManager.AddProfilesNotYetSaved"/>). The delimiter comes across
/// only while this user's delimiter is still the default that the first launch wrote
/// (<see cref="DaqifiSettings.CsvDelimiterIsFirstLaunchDefault"/>). A delimiter that the user set,
/// a comma included, is kept, and so is one in a settings file from an earlier build, which does
/// not say whether it was set.
/// </para>
/// <para>
/// <b>Once per part.</b> The profiles and the delimiter each have a marker, written when that part
/// is done. Without one, a profile that the user deletes after the import would come back at the
/// next Import, and a comma that the user chooses after it would be changed again. They are two
/// markers, so a retry after one part failed cannot undo what the user changed in the other (#512
/// review). A part is not done when an old file could not be opened or this user's file could not
/// be written, so the next Import tries it again. Rereading a damaged file gives the same result,
/// so that part counts as done.
/// </para>
/// </remarks>
public sealed class PreviousSettingsImport
{
    /// <summary>The offer's text when there are no sessions to import, only profiles or settings.</summary>
    public const string OfferMessage = "Found saved profiles and settings from an earlier DAQiFi version.";

    /// <summary>The retry's text when a part did not finish (an old file locked, say).</summary>
    public const string RetryMessage = "Couldn't import all the saved profiles and settings yet.";

    /// <summary>Marks the profiles as done, in the per-user data folder.</summary>
    internal const string ProfilesMarkerFileName = ".previous-settings-import-profiles-done";

    /// <summary>Marks the delimiter as done, in the per-user data folder.</summary>
    internal const string DelimiterMarkerFileName = ".previous-settings-import-delimiter-done";

    /// <summary>The profiles file, by the same name in both folders.</summary>
    internal const string ProfilesFileName = "DAQifiProfilesConfiguration.xml";

    /// <summary>The settings file that holds the delimiter, by the same name in both folders.</summary>
    internal const string SettingsFileName = "DAQifiConfiguration.xml";

    private readonly string _markerDirectory;
    private readonly IAppLogger _appLogger;

    /// <param name="previousDirectory">
    /// The earlier version's folder, or <c>null</c> when there is none to read (not Windows, or a
    /// <c>DAQIFI_DATA_DIR</c> override is in effect).
    /// </param>
    /// <param name="markerDirectory">Where the "done" markers are kept.</param>
    /// <param name="appLogger">Where skipped files and failures are reported.</param>
    internal PreviousSettingsImport(string? previousDirectory, string markerDirectory, IAppLogger? appLogger = null)
    {
        PreviousDirectory = previousDirectory;
        _markerDirectory = markerDirectory;
        _appLogger = appLogger ?? AppLogger.Instance;
    }

    /// <summary>The earlier version's folder, or <c>null</c> when this import does nothing.</summary>
    internal string? PreviousDirectory { get; }

    /// <summary>
    /// The import for this PC. Windows only, because no other platform ever used
    /// <c>%ProgramData%</c>. Does nothing when <c>DAQIFI_DATA_DIR</c> is set, because that override
    /// marks a test or tooling run, which must never pick up the real machine's settings. The same
    /// rules as <see cref="PreviousSessionImport.ForThisPc"/>.
    /// </summary>
    public static PreviousSettingsImport ForThisPc()
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
            // Nothing to read means nothing to import, never a failed Import.
        }

        return new PreviousSettingsImport(previous, AppDataPaths.DataDirectory);
    }

    /// <summary>
    /// Whether a part that is not done still has something this user lacks, so the bell offers
    /// Import even when there are no sessions to copy (#512 review). Reads the old files and this
    /// user's profiles file, so call it off the UI thread. Never throws.
    /// </summary>
    /// <param name="currentProfilesPath">This user's profiles file.</param>
    /// <param name="settings">This user's settings.</param>
    public bool HasSettingsToImport(string currentProfilesPath, DaqifiSettings settings)
    {
        try
        {
            return PreviousDirectory is not null
                   && (ProfilesArePending(currentProfilesPath) || DelimiterIsPending(settings));
        }
        catch (Exception ex)
        {
            _appLogger.Warning(ex, "Could not check for profiles and settings from an earlier DAQiFi version");
            return false;
        }
    }

    /// <summary>
    /// Copies the old profiles and delimiter that this user does not have, each part unless its
    /// marker says it is done. Reads the old files on a worker thread, then changes
    /// <paramref name="profiles"/> and <paramref name="settings"/> on the calling thread, which in
    /// the app is the UI thread that the Profiles pane is bound to. Never throws: a failure is
    /// logged, <see cref="SettingsImportOutcome.Complete"/> is false, and the next Import tries again.
    /// </summary>
    public async Task<SettingsImportOutcome> ImportAsync(LoggingManager profiles, DaqifiSettings settings)
    {
        var outcome = new SettingsImportOutcome();
        try
        {
            if (PreviousDirectory is null)
            {
                return outcome;
            }

            if (!IsDone(ProfilesMarkerFileName))
            {
                Finish(ProfilesMarkerFileName, await ImportProfilesAsync(profiles, outcome), outcome);
            }

            if (!IsDone(DelimiterMarkerFileName))
            {
                Finish(DelimiterMarkerFileName, await ImportDelimiterAsync(settings, outcome), outcome);
            }

            _appLogger.Information(
                $"Import from an earlier DAQiFi version: {outcome.ProfilesAdded} profile(s) copied; " +
                $"CSV delimiter {(outcome.DelimiterCopied ? "copied" : "not changed")}; " +
                $"{(outcome.Complete ? "done" : "to be tried again")}.");
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Importing settings from an earlier DAQiFi version failed");
            outcome.Complete = false;
        }

        return outcome;
    }

    /// <summary>
    /// Records that the import is finished with, both parts, so it is not offered again: what the
    /// user's Dismiss means. Best effort.
    /// </summary>
    public void MarkDone()
    {
        MarkDone(ProfilesMarkerFileName);
        MarkDone(DelimiterMarkerFileName);
    }

    /// <returns>Whether the profiles part is done.</returns>
    private async Task<bool> ImportProfilesAsync(LoggingManager profiles, SettingsImportOutcome outcome)
    {
        var (oldProfiles, read) = await Task.Run(() =>
            ReadOld(ProfilesFileName, ProfileXmlStore.ReadWithoutRepair, ProfileXmlStore.IsUnreadableContent, []));
        if (!read)
        {
            return false;
        }

        try
        {
            outcome.ProfilesAdded = profiles.AddProfilesNotYetSaved(oldProfiles).Count;
            return true;
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Saving the profiles from an earlier DAQiFi version failed");
            return false;
        }
    }

    /// <returns>Whether the delimiter part is done.</returns>
    private async Task<bool> ImportDelimiterAsync(DaqifiSettings settings, SettingsImportOutcome outcome)
    {
        var (oldDelimiter, read) = await Task.Run(() =>
            ReadOld(SettingsFileName, ReadOldDelimiter, DaqifiSettings.IsUnreadableContent, null));
        if (!read)
        {
            return false;
        }

        if (!WouldReplace(oldDelimiter, settings))
        {
            return true;
        }

        if (!settings.CsvDelimiterOptions.Contains(oldDelimiter!))
        {
            _appLogger.Warning(
                $"The earlier DAQiFi version's CSV delimiter \"{oldDelimiter}\" is not one this app supports, so it was not copied.");
            return true;
        }

        // The save result, not the setter: a delimiter that is only in memory is lost at the next
        // launch, so it is not done until it is on disk (#512 review).
        if (!settings.TrySetCsvDelimiter(oldDelimiter!))
        {
            _appLogger.Warning("The CSV delimiter from an earlier DAQiFi version could not be saved; the next Import tries again.");
            return false;
        }

        outcome.DelimiterCopied = true;
        return true;
    }

    /// <summary>
    /// Whether the old delimiter would replace this user's: there is one, it differs, and this
    /// user's is still the first-launch default. The same test for the offer and the import.
    /// </summary>
    private static bool WouldReplace(string? oldDelimiter, DaqifiSettings settings) =>
        oldDelimiter is not null
        && oldDelimiter != settings.CsvDelimiter
        && settings.CsvDelimiterIsFirstLaunchDefault;

    private bool ProfilesArePending(string currentProfilesPath)
    {
        if (IsDone(ProfilesMarkerFileName))
        {
            return false;
        }

        List<Profile> oldProfiles;
        try
        {
            oldProfiles = ProfileXmlStore.ReadWithoutRepair(Path.Combine(PreviousDirectory!, ProfilesFileName));
        }
        catch (Exception ex)
        {
            // Damaged: nothing will ever come of it. Locked: the Import can try, and says if it failed.
            return !ProfileXmlStore.IsUnreadableContent(ex);
        }

        var here = ProfileXmlStore.ReadWithoutRepair(currentProfilesPath).Select(p => p.ProfileId).ToHashSet();
        return oldProfiles.Any(p => !here.Contains(p.ProfileId));
    }

    private bool DelimiterIsPending(DaqifiSettings settings)
    {
        if (IsDone(DelimiterMarkerFileName))
        {
            return false;
        }

        string? oldDelimiter;
        try
        {
            oldDelimiter = ReadOldDelimiter(Path.Combine(PreviousDirectory!, SettingsFileName));
        }
        catch (Exception ex)
        {
            return !DaqifiSettings.IsUnreadableContent(ex);
        }

        return WouldReplace(oldDelimiter, settings) && settings.CsvDelimiterOptions.Contains(oldDelimiter!);
    }

    private static string? ReadOldDelimiter(string path) => DaqifiSettings.ReadStoredDelimiter(path)?.Value;

    /// <summary>
    /// Reads one old file. Returns <paramref name="none"/> for a file that is damaged or that could
    /// not be reached, and says which: a damaged file counts as read (rereading it gives the same
    /// result), and one that could not be reached is tried again at the next Import.
    /// </summary>
    private (T Value, bool Done) ReadOld<T>(string fileName, Func<string, T> read, Func<Exception, bool> isDamaged, T none)
    {
        var path = Path.Combine(PreviousDirectory!, fileName);
        try
        {
            return (read(path), true);
        }
        catch (Exception ex) when (isDamaged(ex))
        {
            // Warning, not Error: a damaged file is a condition of that folder, not a fault in this
            // app, so it raises no crash report. The file stays as it is.
            _appLogger.Warning(ex, $"The earlier DAQiFi version's file {path} could not be read, so it was skipped. The file was not changed.");
            return (none, true);
        }
        catch (Exception ex)
        {
            _appLogger.Warning(ex, $"The earlier DAQiFi version's file {path} could not be opened; the next Import tries again.");
            return (none, false);
        }
    }

    /// <summary>Writes a done part's marker, or records that the import is not complete.</summary>
    private void Finish(string markerFileName, bool done, SettingsImportOutcome outcome)
    {
        if (done)
        {
            MarkDone(markerFileName);
        }
        else
        {
            outcome.Complete = false;
        }
    }

    private bool IsDone(string markerFileName) => File.Exists(Path.Combine(_markerDirectory, markerFileName));

    /// <summary>Records that one part is done, so it does not run again. Best effort.</summary>
    private void MarkDone(string markerFileName)
    {
        try
        {
            Directory.CreateDirectory(_markerDirectory);
            File.WriteAllText(Path.Combine(_markerDirectory, markerFileName), string.Empty);
        }
        catch (Exception ex)
        {
            // Runs again at the next Import. Harmless: what is already here is skipped.
            _appLogger.Warning(ex, "Could not record that part of the settings import from an earlier DAQiFi version is done");
        }
    }
}

/// <summary>What <see cref="PreviousSettingsImport.ImportAsync"/> brought over.</summary>
public sealed class SettingsImportOutcome
{
    /// <summary>How many profiles were copied.</summary>
    public int ProfilesAdded { get; set; }

    /// <summary>Whether the CSV delimiter was copied and saved.</summary>
    public bool DelimiterCopied { get; set; }

    /// <summary>
    /// Whether every part is done. <c>false</c> when an old file could not be opened or this user's
    /// file could not be written; the Import is then offered again.
    /// </summary>
    public bool Complete { get; set; } = true;
}
