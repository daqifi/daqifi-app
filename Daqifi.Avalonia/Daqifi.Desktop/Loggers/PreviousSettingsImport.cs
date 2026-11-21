// Downstream addition (#512) — no upstream counterpart, so no `// @port:` marker.

using Daqifi.Desktop.Common;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Models;

namespace Daqifi.Desktop.Logger;

/// <summary>
/// Brings saved logging profiles and the CSV delimiter over from the folder that an earlier DAQiFi
/// version used, <c>%ProgramData%\DAQiFi</c>. It runs when the user clicks Import on the offer
/// that <see cref="PreviousSessionImport"/> makes, before the sessions are copied.
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
/// only while this user's delimiter is still the default (a comma). The app writes that default
/// at the first launch, so the file cannot show whether the user chose it. A comma is already
/// selected in Settings, so a user chooses it only by changing to a semicolon and back. Before
/// the import, that one choice looks like the default and is changed.
/// </para>
/// <para>
/// <b>Once.</b> When an import is complete, a marker stops a second one. Without it, a profile that
/// the user deletes after the import would come back at the next Import, and a comma that the user
/// chooses after it would be changed again. The offer can come back after an Import (a session
/// still recording, too little disk space), so a second Import is not rare. An import that could
/// not reach an old file, or could not write this user's profiles, is not complete, so the next
/// Import tries again. Rereading a damaged file gives the same result, so it counts as done.
/// </para>
/// </remarks>
public sealed class PreviousSettingsImport
{
    /// <summary>Marks the import as done, in the per-user data folder.</summary>
    internal const string MarkerFileName = ".previous-settings-import-done";

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
    /// <param name="markerDirectory">Where the "done" marker is kept.</param>
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
    /// Copies the old profiles and delimiter that this user does not have. Reads the old files on a
    /// worker thread, then changes <paramref name="profiles"/> and <paramref name="settings"/> on
    /// the calling thread, which in the app is the UI thread that the Profiles pane is bound to.
    /// Never throws: a failure is logged, and the next Import tries again.
    /// </summary>
    public async Task<SettingsImportOutcome> ImportAsync(LoggingManager profiles, DaqifiSettings settings)
    {
        var outcome = new SettingsImportOutcome();
        try
        {
            if (PreviousDirectory is null || File.Exists(Path.Combine(_markerDirectory, MarkerFileName)))
            {
                return outcome;
            }

            // A file that is not there reads as empty, so a missing folder needs no check of its own.
            var (oldProfiles, profilesDone) = await Task.Run(() =>
                ReadOld(ProfilesFileName, ProfileXmlStore.ReadWithoutRepair, ProfileXmlStore.IsUnreadableContent, []));
            var (oldDelimiter, delimiterDone) = await Task.Run(() =>
                ReadOld(SettingsFileName, DaqifiSettings.ReadStoredDelimiter, DaqifiSettings.IsUnreadableContent, null));

            var complete = profilesDone && delimiterDone;

            try
            {
                outcome.ProfilesAdded = profiles.AddProfilesNotYetSaved(oldProfiles).Count;
            }
            catch (Exception ex)
            {
                _appLogger.Error(ex, "Saving the profiles from an earlier DAQiFi version failed");
                complete = false;
            }

            if (oldDelimiter is not null && oldDelimiter != settings.CsvDelimiter && settings.HasDefaultCsvDelimiter)
            {
                if (settings.CsvDelimiterOptions.Contains(oldDelimiter))
                {
                    settings.CsvDelimiter = oldDelimiter;
                    outcome.DelimiterCopied = true;
                }
                else
                {
                    _appLogger.Warning(
                        $"The earlier DAQiFi version's CSV delimiter \"{oldDelimiter}\" is not one this app supports, so it was not copied.");
                }
            }

            _appLogger.Information(
                $"Import from an earlier DAQiFi version: {outcome.ProfilesAdded} profile(s) copied; " +
                $"CSV delimiter {(outcome.DelimiterCopied ? "copied" : "not changed")}.");

            if (complete)
            {
                MarkDone();
            }
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Importing settings from an earlier DAQiFi version failed");
        }

        return outcome;
    }

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

    /// <summary>Records that the import is done, so it does not run again. Best effort.</summary>
    private void MarkDone()
    {
        try
        {
            Directory.CreateDirectory(_markerDirectory);
            File.WriteAllText(Path.Combine(_markerDirectory, MarkerFileName), string.Empty);
        }
        catch (Exception ex)
        {
            // Runs again at the next Import. Harmless: what is already here is skipped.
            _appLogger.Warning(ex, "Could not record that the settings import from an earlier DAQiFi version is done");
        }
    }
}

/// <summary>What <see cref="PreviousSettingsImport.ImportAsync"/> brought over.</summary>
public sealed class SettingsImportOutcome
{
    /// <summary>How many profiles were copied.</summary>
    public int ProfilesAdded { get; set; }

    /// <summary>Whether the CSV delimiter was copied.</summary>
    public bool DelimiterCopied { get; set; }
}
