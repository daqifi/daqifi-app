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
/// <b>Once per part.</b> The profiles and the delimiter each have a marker. Without one, a profile
/// that the user deletes after the import would come back at the next Import. They are two
/// markers, so a retry after one part failed cannot undo what the user changed in the other (#512
/// review). A marker is written before its part does any work and taken away if the work fails,
/// so no work is ever done without a record (#512 review round 2). A part fails when an old file
/// could not be opened or this user's file could not be written, and the next Import tries it
/// again. Rereading a damaged file gives the same result, so that part counts as done.
/// </para>
/// <para>
/// <b>The delimiter is decided against the file, not memory.</b> Another running copy of the app
/// may have saved the user's choice after this copy loaded its settings, so
/// <see cref="DaqifiSettings.ImportCsvDelimiter"/> reads the file again under the settings
/// writers' lock before it replaces anything (#512 review round 2).
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

            await RunPartAsync(ProfilesMarkerFileName, () => ImportProfilesAsync(profiles, outcome), outcome);
            await RunPartAsync(DelimiterMarkerFileName, () => ImportDelimiterAsync(settings, outcome), outcome);

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
    /// user's Dismiss means. Best effort: a record that cannot be written means the offer comes
    /// back at the next launch.
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

        // Decided again under the settings writers' lock, from what the file says now, not from
        // what this copy loaded at launch: another copy of the app may have saved the user's
        // choice since (#512 review round 2). And not done until it is on disk (#512 review).
        switch (settings.ImportCsvDelimiter(oldDelimiter!))
        {
            case DaqifiSettings.DelimiterImportResult.Copied:
                outcome.DelimiterCopied = true;
                return true;
            case DaqifiSettings.DelimiterImportResult.KeptChosen:
                return true;
            default:
                _appLogger.Warning("The CSV delimiter from an earlier DAQiFi version could not be saved; the next Import tries again.");
                return false;
        }
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

        // Nothing to compare, so this user's file is not read at all (#512 review round 2).
        if (oldProfiles.Count == 0)
        {
            return false;
        }

        HashSet<Guid> here;
        try
        {
            here = ProfileXmlStore.ReadWithoutRepair(currentProfilesPath).Select(p => p.ProfileId).ToHashSet();
        }
        catch
        {
            // This user's file is busy or unreadable, so this check cannot tell. Offer the Import:
            // it can try, and it keeps a retry when it fails. A failure here must not hide the
            // delimiter from the offer either (#512 review round 2).
            return true;
        }

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

    /// <summary>
    /// Runs one part, unless its marker says it is done.
    /// </summary>
    /// <remarks>
    /// The marker is written <b>first</b>. A part whose work is done but not recorded could run
    /// again at a later Import and undo what the user changed since, such as a profile they
    /// deleted (#512 review round 2). So a marker that cannot be written stops the part, and the
    /// import is not complete. When the work then fails, the marker is taken away again, and the
    /// next Import tries the part again.
    /// </remarks>
    private async Task RunPartAsync(string markerFileName, Func<Task<bool>> work, SettingsImportOutcome outcome)
    {
        if (IsDone(markerFileName))
        {
            return;
        }

        if (!MarkDone(markerFileName))
        {
            outcome.Complete = false;
            return;
        }

        if (!await work())
        {
            outcome.Complete = false;
            Unmark(markerFileName);
        }
    }

    private bool IsDone(string markerFileName) => File.Exists(Path.Combine(_markerDirectory, markerFileName));

    /// <summary>Records that one part is done, so it does not run again.</summary>
    /// <returns>Whether the record was written.</returns>
    private bool MarkDone(string markerFileName)
    {
        try
        {
            Directory.CreateDirectory(_markerDirectory);
            File.WriteAllText(Path.Combine(_markerDirectory, markerFileName), string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _appLogger.Warning(ex, "Could not record a part of the settings import from an earlier DAQiFi version; it is tried again later");
            return false;
        }
    }

    /// <summary>Takes a part's marker away after its work failed, so that the next Import tries it again.</summary>
    private void Unmark(string markerFileName)
    {
        try
        {
            File.Delete(Path.Combine(_markerDirectory, markerFileName));
        }
        catch (Exception ex)
        {
            // The part now looks done without its work, so it does not run again. Rare, because the
            // folder accepted the marker a moment ago, and logged so that it can be found.
            _appLogger.Error(ex, "Could not take back the marker of a failed part of the settings import from an earlier DAQiFi version; that part will not run again");
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
