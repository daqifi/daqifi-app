// Downstream addition (#512) — no upstream counterpart, so no `// @port:` marker.

using Daqifi.Desktop.Models;

namespace Daqifi.Desktop.Logger;

/// <summary>
/// The one Import offer for everything that an earlier DAQiFi version left in
/// <c>%ProgramData%\DAQiFi</c>: the sessions (<see cref="PreviousSessionImport"/>) and the saved
/// profiles and CSV delimiter (<see cref="PreviousSettingsImport"/>).
/// </summary>
/// <remarks>
/// The bell offers Import while either part has something left, and keeps a retry while either
/// did not finish (#512 review). Before, only sessions opened the offer, and the result of the
/// settings part was not looked at: a locked old profiles file, or a PC with no sessions to copy,
/// left the profiles with no way to bring them over.
/// </remarks>
public sealed class PreviousVersionImport
{
    private readonly Func<LoggingManager> _profiles;
    private readonly Func<DaqifiSettings> _settings;

    /// <param name="sessions">The session import.</param>
    /// <param name="settingsImport">The profiles and delimiter import.</param>
    /// <param name="profiles">This user's profiles. Called on the UI thread for the import, and off it for the check.</param>
    /// <param name="settings">This user's settings. Called as above.</param>
    internal PreviousVersionImport(
        PreviousSessionImport sessions,
        PreviousSettingsImport settingsImport,
        Func<LoggingManager> profiles,
        Func<DaqifiSettings> settings)
    {
        Sessions = sessions;
        SettingsImport = settingsImport;
        _profiles = profiles;
        _settings = settings;
    }

    /// <summary>The import for this PC. Does nothing off Windows or under a <c>DAQIFI_DATA_DIR</c> override.</summary>
    public static PreviousVersionImport ForThisPc(string currentDatabase) =>
        new(PreviousSessionImport.ForThisPc(currentDatabase), PreviousSettingsImport.ForThisPc(),
            () => LoggingManager.Instance, () => DaqifiSettings.Instance);

    /// <summary>The session part.</summary>
    public PreviousSessionImport Sessions { get; }

    /// <summary>The profiles and delimiter part.</summary>
    public PreviousSettingsImport SettingsImport { get; }

    /// <summary>
    /// The offer's text, or <c>null</c> when neither part has anything to bring. Reads files, so
    /// call it off the UI thread. Never throws.
    /// </summary>
    public string? CheckOffer()
    {
        try
        {
            if (Sessions.HasSessionsToImport())
            {
                return PreviousSessionImport.OfferMessage;
            }

            return SettingsImport.HasSettingsToImport(_profiles().ProfilesFilePath, _settings())
                ? PreviousSettingsImport.OfferMessage
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies the profiles and delimiter on the calling thread (the UI thread in the app), then the
    /// sessions on a worker thread when any may be left.
    /// </summary>
    /// <returns>What the profiles and delimiter part did.</returns>
    /// <exception cref="Exception">What <see cref="PreviousSessionImport.ImportAsync"/> throws.</exception>
    public async Task<SettingsImportOutcome> ImportAsync(ImportOutcome sessions, IProgress<double>? progress)
    {
        var settings = await SettingsImport.ImportAsync(_profiles(), _settings());

        // An offer made for profiles alone has no sessions to copy, and the session import refuses
        // to start without an earlier database. A check that failed (null) still tries, so that
        // the failure shows.
        if (await Task.Run(Sessions.CheckForSessionsToImport) != false)
        {
            await Task.Run(() => Sessions.ImportAsync(sessions, progress));
        }

        return settings;
    }

    /// <summary>Records that the user dismissed the offer: neither part is offered again.</summary>
    public void MarkDone()
    {
        Sessions.MarkDone();
        SettingsImport.MarkDone(_profiles(), _settings());
    }

    /// <summary>The line the bell shows when an Import finished.</summary>
    public static string ResultMessage(int sessionsImported, SettingsImportOutcome settings) =>
        sessionsImported switch
        {
            0 when settings.ProfilesAdded > 0 || settings.DelimiterCopied => "Imported the saved profiles and settings.",
            0 => "Nothing new to import yet.",
            1 => "Imported 1 session.",
            var n => $"Imported {n} sessions.",
        };
}
