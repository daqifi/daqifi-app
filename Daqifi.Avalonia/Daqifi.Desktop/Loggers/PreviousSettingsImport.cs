// Downstream addition (#512) — no upstream counterpart, so no `// @port:` marker.

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
/// or repaired, as the app does with its own files. A file that cannot be opened (locked, no
/// access) is not taken for an absent one: that part fails, and the next Import tries again.
/// </para>
/// <para>
/// <b>Nothing that the user has is replaced.</b> A profile comes across only when no profile with
/// its ID is here (<see cref="LoggingManager.AddProfilesNotYetSaved"/>). The delimiter comes across
/// only while this user's settings file says it is the first-launch default, read again under the
/// settings writers' lock (<see cref="DaqifiSettings.ImportCsvDelimiter"/>), so a choice saved by
/// another running copy is kept too.
/// </para>
/// <para>
/// <b>Each part's record is in the file that the part writes, in the same write.</b> The profiles
/// part sets <see cref="ProfileXmlStore.ImportRecordAttribute"/> in this user's profiles file in
/// the write that adds the profiles. The delimiter part's record is the "chosen" flag that the
/// write of the delimiter sets. A run cut off at any point therefore leaves each part either done
/// and recorded, or not done and not recorded, and a later Import does exactly the parts that are
/// not done (#512 review round 3). A part that is recorded does not run again, so a profile that
/// the user deletes after the import stays deleted (#512 review). Two earlier designs used marker
/// files, and each left a window: written after the work, a lost marker let a later run undo the
/// user's changes; written before it, a marker could stand without the work.
/// </para>
/// </remarks>
public sealed class PreviousSettingsImport
{
    /// <summary>The offer's text when there are no sessions to import, only profiles or settings.</summary>
    public const string OfferMessage = "Found saved profiles and settings from an earlier DAQiFi version.";

    /// <summary>The retry's text when a part did not finish (an old file locked, say).</summary>
    public const string RetryMessage = "Couldn't import all the saved profiles and settings yet.";

    /// <summary>The profiles file, by the same name in both folders.</summary>
    internal const string ProfilesFileName = "DAQifiProfilesConfiguration.xml";

    /// <summary>The settings file that holds the delimiter, by the same name in both folders.</summary>
    internal const string SettingsFileName = "DAQifiConfiguration.xml";

    /// <summary>The points of an import at which a test can cut the run off, in order.</summary>
    internal enum Step
    {
        /// <summary>The old profiles were read; nothing is written yet.</summary>
        ProfilesRead,

        /// <summary>Inside the profiles write, under the lock, before the file is replaced.</summary>
        ProfilesWriting,

        /// <summary>The profiles and their record are on disk.</summary>
        ProfilesWritten,

        /// <summary>The old delimiter was read; the settings file is not written yet.</summary>
        DelimiterRead,

        /// <summary>The delimiter part is finished.</summary>
        DelimiterWritten,
    }

    private readonly IAppLogger _appLogger;

    /// <param name="previousDirectory">
    /// The earlier version's folder, or <c>null</c> when there is none to read (not Windows, or a
    /// <c>DAQIFI_DATA_DIR</c> override is in effect).
    /// </param>
    /// <param name="appLogger">Where skipped files and failures are reported.</param>
    internal PreviousSettingsImport(string? previousDirectory, IAppLogger? appLogger = null)
    {
        PreviousDirectory = previousDirectory;
        _appLogger = appLogger ?? AppLogger.Instance;
    }

    /// <summary>The earlier version's folder, or <c>null</c> when this import does nothing.</summary>
    internal string? PreviousDirectory { get; }

    /// <summary>
    /// Called at each <see cref="Step"/>. Tests throw from it to stand for a closed app or a power
    /// cut at that point. <c>null</c> in the app.
    /// </summary>
    internal Action<Step>? AtStep { get; init; }

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

        return new PreviousSettingsImport(previous);
    }

    /// <summary>
    /// Whether a part still has something this user lacks, so the bell offers Import even when
    /// there are no sessions to copy (#512 review). Reads the old files and this user's profiles
    /// file, so call it off the UI thread. Never throws.
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
    /// Copies the old profiles and delimiter that this user does not have. Reads the old files on a
    /// worker thread, then changes <paramref name="profiles"/> and <paramref name="settings"/> on
    /// the calling thread, which in the app is the UI thread that the Profiles pane is bound to.
    /// Never throws: a failure is logged, <see cref="SettingsImportOutcome.Complete"/> is false, and
    /// the next Import tries again.
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

            if (!await ImportProfilesAsync(profiles, outcome))
            {
                outcome.Complete = false;
            }

            if (!await ImportDelimiterAsync(settings, outcome))
            {
                outcome.Complete = false;
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
    /// user's Dismiss means. The records go where the import's own records go: the profiles file,
    /// and the delimiter's "chosen" flag. Best effort: a record that cannot be written means the
    /// offer comes back at the next launch.
    /// </summary>
    public void MarkDone(LoggingManager profiles, DaqifiSettings settings)
    {
        profiles.RecordEarlierVersionImport();
        settings.KeepCsvDelimiter();
    }

    /// <returns>Whether the profiles part is finished.</returns>
    private async Task<bool> ImportProfilesAsync(LoggingManager profiles, SettingsImportOutcome outcome)
    {
        var (oldProfiles, read) = await Task.Run(() =>
            ReadOld(ProfilesFileName, ProfileXmlStore.ReadWithoutRepair, ProfileXmlStore.IsUnreadableContent, []));
        if (!read)
        {
            return false;
        }

        AtStep?.Invoke(Step.ProfilesRead);

        try
        {
            // One write adds the profiles this user lacks and records that the import ran. When the
            // file already has the record, it adds nothing.
            outcome.ProfilesAdded = profiles.AddProfilesNotYetSaved(InsideTheWrite(oldProfiles)).Count;
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Saving the profiles from an earlier DAQiFi version failed");
            return false;
        }

        AtStep?.Invoke(Step.ProfilesWritten);
        return true;
    }

    /// <summary>
    /// The candidates, read inside the profiles write: <see cref="LoggingManager.AddProfilesNotYetSaved"/>
    /// goes through them under the lock, before the file is replaced, so this is where
    /// <see cref="Step.ProfilesWriting"/> falls.
    /// </summary>
    private IEnumerable<Profile> InsideTheWrite(List<Profile> candidates)
    {
        foreach (var profile in candidates)
        {
            yield return profile;
        }

        AtStep?.Invoke(Step.ProfilesWriting);
    }

    /// <returns>Whether the delimiter part is finished.</returns>
    private async Task<bool> ImportDelimiterAsync(DaqifiSettings settings, SettingsImportOutcome outcome)
    {
        var (oldDelimiter, read) = await Task.Run(() =>
            ReadOld(SettingsFileName, ReadOldDelimiter, DaqifiSettings.IsUnreadableContent, null));
        if (!read)
        {
            return false;
        }

        AtStep?.Invoke(Step.DelimiterRead);

        var finished = CopyDelimiter(oldDelimiter, settings, outcome);
        if (finished)
        {
            AtStep?.Invoke(Step.DelimiterWritten);
        }

        return finished;
    }

    private bool CopyDelimiter(string? oldDelimiter, DaqifiSettings settings, SettingsImportOutcome outcome)
    {
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
        List<Profile> oldProfiles;
        try
        {
            oldProfiles = ProfileXmlStore.ReadWithoutRepair(Path.Combine(PreviousDirectory!, ProfilesFileName));
        }
        catch (Exception ex)
        {
            // Damaged: nothing will ever come of it. Locked or no access: the Import can try, and
            // says if it failed.
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
            var current = ProfileXmlStore.ReadWithoutRepair(currentProfilesPath, out var recorded);
            if (recorded)
            {
                return false;
            }

            here = current.Select(p => p.ProfileId).ToHashSet();
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
    /// result), and one that could not be reached (locked, no access) is tried again at the next
    /// Import.
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
}

/// <summary>What <see cref="PreviousSettingsImport.ImportAsync"/> brought over.</summary>
public sealed class SettingsImportOutcome
{
    /// <summary>How many profiles were copied.</summary>
    public int ProfilesAdded { get; set; }

    /// <summary>Whether the CSV delimiter was copied and saved.</summary>
    public bool DelimiterCopied { get; set; }

    /// <summary>
    /// Whether every part is finished. <c>false</c> when an old file could not be opened or this
    /// user's file could not be written; the Import is then offered again.
    /// </summary>
    public bool Complete { get; set; } = true;
}
