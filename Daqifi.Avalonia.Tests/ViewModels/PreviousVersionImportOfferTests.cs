using CommunityToolkit.Mvvm.Input;
using Daqifi.Desktop.DialogService;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// #512 review, P1: "Keep unfinished settings imports reachable from the bell". The bell's Import
/// offer used to come only from sessions, and the Import command did not look at the settings
/// part's result. So a locked old profiles file left no retry once the sessions were done, and a
/// PC with profiles but no sessions to copy got no offer at all.
/// </summary>
/// <remarks>
/// These go through the real <see cref="DaqifiViewModel"/> command and the real
/// <see cref="PreviousVersionImport"/>, with throwaway folders. The session part has no earlier
/// database, as on a PC with nothing but profiles to bring.
/// </remarks>
[Collection(AppHostCollection.Name)]
public sealed class PreviousVersionImportOfferTests : IDisposable
{
    private static readonly Guid BenchId = Guid.Parse("6f1c2a8e-3b44-4d0a-9a51-0c2b7d9e1f01");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "previous-version-offer-" + Guid.NewGuid().ToString("N"));

    private readonly RecordingAppLogger _log = new();

    private string Old => Path.Combine(_root, "ProgramData", "DAQiFi");
    private string Current => Path.Combine(_root, "LocalAppData", "DAQiFi");
    private string OldProfiles => Path.Combine(Old, PreviousSettingsImport.ProfilesFileName);

    public PreviousVersionImportOfferTests()
    {
        Directory.CreateDirectory(Old);
        Directory.CreateDirectory(Current);
        File.WriteAllText(OldProfiles, $"""
            <Profiles>
              <Profile>
                <Name>Bench rig</Name>
                <ProfileID>{BenchId}</ProfileID>
                <CreatedOn>2025-03-04T05:06:07</CreatedOn>
                <Devices />
              </Profile>
            </Profiles>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class UnusedContextFactory : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() =>
            throw new InvalidOperationException("Profile persistence must not touch the database.");
    }

    private LoggingManager Manager()
    {
        var manager = new LoggingManager(new UnusedContextFactory(), Path.Combine(Current, PreviousSettingsImport.ProfilesFileName));
        manager.LoadSavedProfiles();
        return manager;
    }

    private PreviousVersionImport Import(LoggingManager manager, DaqifiSettings settings) => new(
        new PreviousSessionImport(null, Path.Combine(Current, "DAQiFiDatabase.db"), Current),
        new PreviousSettingsImport(Old, Current, _log),
        () => manager,
        () => settings);

    private static DaqifiViewModel NewViewModel()
    {
        Daqifi.Desktop.App.InitializeMobile();
        return new DaqifiViewModel(new NullDialogService());
    }

    /// <summary>
    /// The reviewer's sequence. Import while the old profiles file is locked: no session is left,
    /// but the bell keeps an Import that can try again, and the next launch offers it too. When
    /// the lock is gone, that same button brings the profile over and the offer is finished.
    /// </summary>
    [Fact]
    public async Task A_locked_profiles_file_keeps_an_Import_in_the_bell_until_it_comes_across()
    {
        var manager = Manager();
        var import = Import(manager, new DaqifiSettings(Path.Combine(Current, PreviousSettingsImport.SettingsFileName)));
        var viewModel = NewViewModel();

        using (new FileStream(OldProfiles, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await viewModel.ImportPreviousSessionsAsync(import);
        }

        var retry = Assert.Single(viewModel.NotificationList);
        Assert.Equal(PreviousSettingsImport.RetryMessage, retry.Message);
        Assert.Equal("Import", retry.ActionText);
        Assert.Empty(manager.SubscribedProfiles);
        Assert.Equal(PreviousSettingsImport.OfferMessage, import.CheckOffer());

        await ((IAsyncRelayCommand)retry.Action!).ExecuteAsync(null);

        Assert.Equal("Bench rig", Assert.Single(manager.SubscribedProfiles).Name);
        var result = Assert.Single(viewModel.NotificationList);
        Assert.Equal("Imported the saved profiles and settings.", result.Message);
        Assert.Null(result.Action);
        Assert.Null(import.CheckOffer());
    }

    /// <summary>
    /// A PC with saved profiles but no sessions to copy still gets the offer, in words that do not
    /// promise sessions.
    /// </summary>
    [Fact]
    public void Profiles_with_no_sessions_to_copy_are_offered()
    {
        var import = Import(Manager(), new DaqifiSettings(Path.Combine(Current, PreviousSettingsImport.SettingsFileName)));

        Assert.Equal(PreviousSettingsImport.OfferMessage, import.CheckOffer());
    }

    /// <summary>Dismiss means "not again", for the profiles and settings as well as the sessions.</summary>
    [Fact]
    public void Dismiss_ends_the_offer_for_both_parts()
    {
        var import = Import(Manager(), new DaqifiSettings(Path.Combine(Current, PreviousSettingsImport.SettingsFileName)));

        import.MarkDone();

        Assert.Null(import.CheckOffer());
    }

    /// <summary>
    /// Sessions still open the offer, with the words they had before, and Dismiss still ends it.
    /// The old database has the release WPF app's schema (as in <c>PreviousSessionImportTests</c>);
    /// this user's database is built by the app's own migrations.
    /// </summary>
    [Fact]
    public void Sessions_still_open_the_offer_and_Dismiss_still_ends_it()
    {
        var previousDatabase = Path.Combine(Old, "DAQiFiDatabase.db");
        var currentDatabase = Path.Combine(Current, "DAQiFiDatabase.db");
        DatabaseMigrator.ApplyMigrations(TestDatabase.Contexts(currentDatabase), currentDatabase);
        Execute(previousDatabase,
            "CREATE TABLE Sessions (ID INTEGER NOT NULL PRIMARY KEY, SessionStart TEXT NOT NULL, Name TEXT NOT NULL, SampleCount INTEGER NULL);" +
            "CREATE TABLE Samples (ID INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, LoggingSessionID INTEGER NOT NULL, Value REAL NOT NULL, " +
            "TimestampTicks INTEGER NOT NULL, DeviceName TEXT NOT NULL, ChannelName TEXT NOT NULL, DeviceSerialNo TEXT NOT NULL, " +
            "Color TEXT NOT NULL, Type INTEGER NOT NULL);" +
            "INSERT INTO Sessions (ID, SessionStart, Name, SampleCount) VALUES (1, '2025-03-04 05:06:07', 'Old run', 1);" +
            "INSERT INTO Samples (ID, LoggingSessionID, Value, TimestampTicks, DeviceName, ChannelName, DeviceSerialNo, Color, Type) " +
            "VALUES (1, 1, 1.5, 10, 'Nq1', 'AI0', 'SN-TEST', '#FF0000', 1);");
        // Nothing for the settings part, so only the sessions can open the offer.
        File.Delete(OldProfiles);
        var manager = Manager();
        var import = new PreviousVersionImport(
            new PreviousSessionImport(previousDatabase, currentDatabase, Current),
            new PreviousSettingsImport(Old, Current, _log),
            () => manager,
            () => new DaqifiSettings(Path.Combine(Current, PreviousSettingsImport.SettingsFileName)));

        Assert.Equal(PreviousSessionImport.OfferMessage, import.CheckOffer());

        import.MarkDone();

        Assert.Null(import.CheckOffer());
    }

    /// <summary>The bell's last line says what came across, sessions or profiles and settings.</summary>
    [Theory]
    [InlineData(0, 0, false, "Nothing new to import yet.")]
    [InlineData(0, 2, false, "Imported the saved profiles and settings.")]
    [InlineData(0, 0, true, "Imported the saved profiles and settings.")]
    [InlineData(1, 2, true, "Imported 1 session.")]
    [InlineData(3, 0, false, "Imported 3 sessions.")]
    public void The_result_line_says_what_came_across(int sessions, int profiles, bool delimiter, string expected)
    {
        var settings = new SettingsImportOutcome { ProfilesAdded = profiles, DelimiterCopied = delimiter };

        Assert.Equal(expected, PreviousVersionImport.ResultMessage(sessions, settings));
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(TestDatabase.ConnectionString(database));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
