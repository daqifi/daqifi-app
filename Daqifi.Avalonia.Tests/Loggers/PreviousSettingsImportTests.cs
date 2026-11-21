using System.ComponentModel;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// #512: Import brought sessions over from <c>%ProgramData%\DAQiFi</c>, but not the saved logging
/// profiles or the CSV delimiter beside them, so the user had to set those up again.
/// </summary>
/// <remarks>
/// <para>
/// Each test has two throwaway folders: <c>ProgramData/DAQiFi</c> holds the old files, in the shape
/// the release WPF app writes, and <c>LocalAppData/DAQiFi</c> is this user's folder. A real
/// <see cref="LoggingManager"/> and a real <see cref="DaqifiSettings"/> run over this user's folder
/// as the app does at startup, so "appears in the app" means it is in
/// <see cref="LoggingManager.SubscribedProfiles"/>, which the Profiles pane binds to.
/// </para>
/// <para>
/// The rules from the ticket: the old files are only read (the release WPF app still uses them),
/// and nothing that the user already has is replaced.
/// </para>
/// </remarks>
public sealed class PreviousSettingsImportTests : IDisposable
{
    private static readonly Guid BenchId = Guid.Parse("6f1c2a8e-3b44-4d0a-9a51-0c2b7d9e1f01");
    private static readonly Guid FieldId = Guid.Parse("0a9d4c3b-7e21-4f6a-8b0c-5d1e2f3a4b02");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "settings-import-" + Guid.NewGuid().ToString("N"));

    private readonly RecordingAppLogger _log = new();

    private string Old => Path.Combine(_root, "ProgramData", "DAQiFi");
    private string Current => Path.Combine(_root, "LocalAppData", "DAQiFi");
    private string OldProfiles => Path.Combine(Old, PreviousSettingsImport.ProfilesFileName);
    private string OldSettings => Path.Combine(Old, PreviousSettingsImport.SettingsFileName);
    private string CurrentProfiles => Path.Combine(Current, PreviousSettingsImport.ProfilesFileName);
    private string CurrentSettings => Path.Combine(Current, PreviousSettingsImport.SettingsFileName);
    private string Marker => Path.Combine(Current, PreviousSettingsImport.MarkerFileName);

    public PreviousSettingsImportTests()
    {
        Directory.CreateDirectory(Old);
        Directory.CreateDirectory(Current);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Profiles are XML, not the database. A test that opens a context has gone wrong.</summary>
    private sealed class UnusedContextFactory : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() =>
            throw new InvalidOperationException("Profile persistence must not touch the database.");
    }

    /// <summary>A manager over this user's profiles file, loaded as at app startup.</summary>
    private LoggingManager Manager(string? profilesPath = null)
    {
        var manager = new LoggingManager(new UnusedContextFactory(), profilesPath ?? CurrentProfiles);
        manager.LoadSavedProfiles();
        return manager;
    }

    /// <summary>This user's settings. Like the app's first launch, this writes the default comma.</summary>
    private DaqifiSettings Settings() => new(CurrentSettings);

    private PreviousSettingsImport Import() => new(Old, Current, _log);

    /// <summary>One profile as the release WPF app writes it (its AddAndRemoveProfileXml).</summary>
    private static string WpfProfile(Guid id, string name, bool withChannels = true) => $"""
          <Profile>
            <Name>{name}</Name>
            <ProfileID>{id}</ProfileID>
            <CreatedOn>2025-03-04T05:06:07</CreatedOn>
            <Devices>
              <Device>
                <DeviceName>Nyquist 1</DeviceName>
                <DevicePartNumber>Nq1</DevicePartNumber>
                <MACAddress>00:11:22:33:44:55</MACAddress>
                <DeviceSerialNo>SERIAL-A</DeviceSerialNo>
                {(withChannels ? "<Channels><Channel><Name>AI0</Name><Type>Analog</Type><IsActive>true</IsActive></Channel></Channels>" : string.Empty)}
                <SamplingFrequency>100</SamplingFrequency>
              </Device>
            </Devices>
          </Profile>
        """;

    private static void WriteProfiles(string path, params string[] profiles) =>
        File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Profiles>\n{string.Join("\n", profiles)}\n</Profiles>");

    private static void WriteDelimiter(string path, string delimiter) =>
        File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<DAQifiSettings><CsvDelimiter>{delimiter}</CsvDelimiter></DAQifiSettings>");

    private static Profile UsersProfile(Guid id, string name) => new()
    {
        Name = name,
        ProfileId = id,
        CreatedOn = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Unspecified),
        Devices =
        [
            new ProfileDevice
            {
                DeviceName = "Nyquist 1",
                DevicePartName = "Nq1",
                MacAddress = "00:11:22:33:44:55",
                DeviceSerialNo = "SERIAL-A",
                SamplingFrequency = 5000,
                Channels = [new ProfileChannel { Name = "AI3", Type = "Analog", IsChannelActive = true, SerialNo = "SERIAL-A" }],
            },
        ],
    };

    /// <summary>Every file in a folder, with its bytes and its last write time.</summary>
    private static Dictionary<string, (byte[] Bytes, DateTime Written)> Snapshot(string directory) =>
        Directory.GetFiles(directory).ToDictionary(
            path => Path.GetFileName(path),
            path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));

    private static void AssertSameFiles(
        Dictionary<string, (byte[] Bytes, DateTime Written)> before,
        Dictionary<string, (byte[] Bytes, DateTime Written)> after)
    {
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (name, file) in before)
        {
            Assert.Equal(file.Bytes, after[name].Bytes);
            Assert.Equal(file.Written, after[name].Written);
        }
    }

    /// <summary>
    /// The ticket's "done when". The profiles show at once, and they are in this user's file, so
    /// they are still there at the next launch. The older WPF shape with no <c>&lt;Channels&gt;</c>
    /// reads too.
    /// </summary>
    [Fact]
    public async Task Profiles_only_in_the_old_folder_are_copied_and_shown()
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"), WpfProfile(FieldId, "Field", withChannels: false));
        var manager = Manager();

        var outcome = await Import().ImportAsync(manager, Settings());

        Assert.Equal(2, outcome.ProfilesAdded);
        Assert.Equal(["Bench rig", "Field"], manager.SubscribedProfiles.Select(p => p.Name));

        var nextLaunch = Manager().SubscribedProfiles;
        Assert.Equal([BenchId, FieldId], nextLaunch.Select(p => p.ProfileId));
        var device = Assert.Single(nextLaunch[0].Devices);
        Assert.Equal(100, device.SamplingFrequency);
        Assert.Equal("AI0", Assert.Single(device.Channels).Name);

        // No settings file in the old folder: that is nothing to read, so the import is done and
        // nothing is reported.
        Assert.True(File.Exists(Marker));
        Assert.Empty(_log.Warnings);
    }

    /// <summary>
    /// A profile with the same ID is the user's own now, edited or not. It is kept exactly as it is,
    /// and only the profile they do not have comes across.
    /// </summary>
    [Fact]
    public async Task A_profile_already_here_is_left_alone()
    {
        var manager = Manager();
        var mine = UsersProfile(BenchId, "Mine");
        Assert.True(manager.SubscribeProfile(mine));
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Theirs"), WpfProfile(FieldId, "Field"));

        var outcome = await Import().ImportAsync(manager, Settings());

        Assert.Equal(1, outcome.ProfilesAdded);
        Assert.Same(mine, manager.SubscribedProfiles.Single(p => p.ProfileId == BenchId));

        var nextLaunch = Manager().SubscribedProfiles;
        Assert.Equal([BenchId, FieldId], nextLaunch.Select(p => p.ProfileId));
        Assert.Equal("Mine", nextLaunch[0].Name);
        Assert.Equal(5000, nextLaunch[0].Devices.Single().SamplingFrequency);
    }

    /// <summary>
    /// After a damaged profiles file was moved aside, the pane still shows the user's profiles
    /// while the new file is empty. A profile on display is the user's, so it is not added again.
    /// </summary>
    [Fact]
    public async Task A_profile_on_display_but_not_in_the_file_is_not_added_again()
    {
        var manager = Manager();
        Assert.True(manager.SubscribeProfile(UsersProfile(BenchId, "Mine")));
        WriteProfiles(CurrentProfiles);
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Theirs"));

        var outcome = await Import().ImportAsync(manager, Settings());

        Assert.Equal(0, outcome.ProfilesAdded);
        Assert.Equal("Mine", Assert.Single(manager.SubscribedProfiles).Name);
    }

    /// <summary>
    /// Another copy of the app can write the file after this one read it. A profile in the file is
    /// the user's, so it is not written a second time.
    /// </summary>
    [Fact]
    public async Task A_profile_that_another_copy_of_the_app_saved_is_not_added_again()
    {
        var manager = Manager();
        Assert.True(Manager().SubscribeProfile(UsersProfile(BenchId, "Mine")));
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Theirs"));

        var outcome = await Import().ImportAsync(manager, Settings());

        Assert.Equal(0, outcome.ProfilesAdded);
        Assert.Equal("Mine", Assert.Single(Manager().SubscribedProfiles).Name);
    }

    /// <summary>
    /// A damaged old file is skipped with a log line, never a crash, and the other file still comes
    /// across. It is not moved aside or repaired as the app's own files are: it is not this app's.
    /// It counts as done, because reading it again gives the same result.
    /// </summary>
    [Theory]
    [InlineData(PreviousSettingsImport.ProfilesFileName)]
    [InlineData(PreviousSettingsImport.SettingsFileName)]
    public async Task A_damaged_old_file_is_skipped_with_a_log_line_and_left_as_it_is(string damagedFile)
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        WriteDelimiter(OldSettings, ";");
        var damaged = Path.Combine(Old, damagedFile);
        File.WriteAllText(damaged, "<Profiles><Profile><Name>Bench");
        var before = Snapshot(Old);
        var manager = Manager();
        var settings = Settings();

        var outcome = await Import().ImportAsync(manager, settings);

        var profilesDamaged = damagedFile == PreviousSettingsImport.ProfilesFileName;
        Assert.Equal(profilesDamaged ? 0 : 1, outcome.ProfilesAdded);
        Assert.Equal(profilesDamaged, outcome.DelimiterCopied);
        Assert.Equal(profilesDamaged ? ";" : ",", settings.CsvDelimiter);
        Assert.Contains(_log.Warnings, warning => warning.Contains(damaged));
        Assert.Empty(_log.Errors);
        AssertSameFiles(before, Snapshot(Old));
        Assert.True(File.Exists(Marker));
    }

    /// <summary>
    /// The release WPF app still uses the old folder, so the import must not change it at all: the
    /// same files, the same bytes, the same times, and no file added.
    /// </summary>
    [Fact]
    public async Task The_old_files_are_byte_identical_afterwards()
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"), WpfProfile(FieldId, "Field"));
        WriteDelimiter(OldSettings, ";");
        File.WriteAllText(Path.Combine(Old, "DAQiFiDatabase.db"), "not read by this import");
        var before = Snapshot(Old);

        var outcome = await Import().ImportAsync(Manager(), Settings());

        Assert.Equal(2, outcome.ProfilesAdded);
        Assert.True(outcome.DelimiterCopied);
        AssertSameFiles(before, Snapshot(Old));
    }

    /// <summary>
    /// The app writes a comma at the first launch, so that is what every user has before they
    /// choose. The old semicolon replaces it and is saved, so it is still there at the next launch.
    /// </summary>
    [Fact]
    public async Task The_old_delimiter_replaces_the_default()
    {
        WriteDelimiter(OldSettings, ";");
        var settings = Settings();

        var outcome = await Import().ImportAsync(Manager(), settings);

        Assert.True(outcome.DelimiterCopied);
        Assert.Equal(";", settings.CsvDelimiter);
        Assert.Equal(";", Settings().CsvDelimiter);
        // No profiles file in the old folder: nothing to read, so the import is done.
        Assert.True(File.Exists(Marker));
    }

    /// <summary>A delimiter the user chose here is theirs, so the old one does not replace it.</summary>
    [Fact]
    public async Task A_delimiter_the_user_chose_is_kept()
    {
        WriteDelimiter(OldSettings, ",");
        var settings = Settings();
        settings.CsvDelimiter = ";";

        var outcome = await Import().ImportAsync(Manager(), settings);

        Assert.False(outcome.DelimiterCopied);
        Assert.Equal(";", settings.CsvDelimiter);
        Assert.Equal(";", Settings().CsvDelimiter);
    }

    /// <summary>
    /// The old file is hand-editable. A delimiter this app does not offer would join every CSV
    /// column, so it is not copied, and the log says why.
    /// </summary>
    [Fact]
    public async Task An_unsupported_old_delimiter_is_not_copied()
    {
        WriteDelimiter(OldSettings, "|");
        var settings = Settings();

        var outcome = await Import().ImportAsync(Manager(), settings);

        Assert.False(outcome.DelimiterCopied);
        Assert.Equal(",", settings.CsvDelimiter);
        Assert.Contains(_log.Warnings, warning => warning.Contains("\"|\""));
    }

    /// <summary>
    /// When the old folder holds nothing the user lacks, the import writes nothing: this user's
    /// files keep their bytes and their times.
    /// </summary>
    [Fact]
    public async Task Nothing_to_bring_leaves_this_users_files_untouched()
    {
        var manager = Manager();
        Assert.True(manager.SubscribeProfile(UsersProfile(BenchId, "Mine")));
        var settings = Settings();
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Theirs"));
        WriteDelimiter(OldSettings, ",");
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(CurrentProfiles, longAgo);
        File.SetLastWriteTimeUtc(CurrentSettings, longAgo);
        var before = Snapshot(Current);

        var outcome = await Import().ImportAsync(manager, settings);

        Assert.Equal(0, outcome.ProfilesAdded);
        Assert.False(outcome.DelimiterCopied);
        AssertSameFiles(before, Snapshot(Current).Where(f => f.Key != PreviousSettingsImport.MarkerFileName).ToDictionary());
    }

    /// <summary>
    /// The offer can come back after an Import, so a second Import is not rare. A profile the user
    /// deleted after the first one must not come back, and a comma chosen after it must stay.
    /// </summary>
    [Fact]
    public async Task A_second_Import_does_not_undo_what_the_user_changed_after_the_first()
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        WriteDelimiter(OldSettings, ";");
        var manager = Manager();
        var settings = Settings();
        await Import().ImportAsync(manager, settings);
        Assert.True(manager.UnsubscribeProfile(manager.SubscribedProfiles.Single()));
        settings.CsvDelimiter = ",";

        var second = await Import().ImportAsync(manager, settings);

        Assert.Equal(0, second.ProfilesAdded);
        Assert.False(second.DelimiterCopied);
        Assert.Empty(manager.SubscribedProfiles);
        Assert.Equal(",", settings.CsvDelimiter);
    }

    /// <summary>
    /// A file the other app has locked can be read a moment later, so the import is not done: no
    /// marker, and the next Import copies what this one could not.
    /// </summary>
    [Theory]
    [InlineData(PreviousSettingsImport.ProfilesFileName)]
    [InlineData(PreviousSettingsImport.SettingsFileName)]
    public async Task An_old_file_that_cannot_be_opened_is_tried_again_at_the_next_Import(string lockedFile)
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        WriteDelimiter(OldSettings, ";");
        var manager = Manager();
        var settings = Settings();

        SettingsImportOutcome first;
        using (new FileStream(Path.Combine(Old, lockedFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            first = await Import().ImportAsync(manager, settings);
        }

        Assert.False(File.Exists(Marker));
        Assert.Contains(_log.Warnings, warning => warning.Contains("tries again"));

        var second = await Import().ImportAsync(manager, settings);

        var profilesLocked = lockedFile == PreviousSettingsImport.ProfilesFileName;
        // The file that could be read came across the first time; the locked one the second time.
        Assert.Equal(profilesLocked ? 0 : 1, first.ProfilesAdded);
        Assert.Equal(profilesLocked, first.DelimiterCopied);
        Assert.Equal(profilesLocked ? 1 : 0, second.ProfilesAdded);
        Assert.Equal(!profilesLocked, second.DelimiterCopied);
        Assert.Equal(";", settings.CsvDelimiter);
        Assert.True(File.Exists(Marker));
    }

    /// <summary>
    /// When this user's profiles cannot be written, nothing came across, so the import is not done
    /// and the next Import tries again. The Import itself still goes on to the sessions.
    /// </summary>
    [Fact]
    public async Task Profiles_that_cannot_be_saved_here_are_tried_again_at_the_next_Import()
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file where a folder should be");
        var manager = Manager(Path.Combine(blocker, PreviousSettingsImport.ProfilesFileName));

        var outcome = await Import().ImportAsync(manager, Settings());

        Assert.Equal(0, outcome.ProfilesAdded);
        Assert.Empty(manager.SubscribedProfiles);
        Assert.Single(_log.Errors);
        Assert.False(File.Exists(Marker));
    }

    /// <summary>Off Windows, and under a <c>DAQIFI_DATA_DIR</c> override, there is nothing to read.</summary>
    [Fact]
    public async Task With_no_old_folder_to_read_the_import_does_nothing()
    {
        // This suite sets DAQIFI_DATA_DIR, and it is not Windows on CI either.
        Assert.Null(PreviousSettingsImport.ForThisPc().PreviousDirectory);
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        var manager = Manager();

        var outcome = await new PreviousSettingsImport(null, Current, _log).ImportAsync(manager, Settings());

        Assert.Equal(0, outcome.ProfilesAdded);
        Assert.Empty(manager.SubscribedProfiles);
        Assert.False(File.Exists(Marker));
        // Nothing is tried, so nothing fails and nothing is reported: not even the summary line.
        Assert.Empty(_log.Errors);
        Assert.Empty(_log.Warnings);
        Assert.Empty(_log.Informations);
    }

    /// <summary>
    /// The Settings pane's view model is built once. When the import sets the delimiter, the pane
    /// must show it, not the comma it showed before. A change made through the pane still shows.
    /// </summary>
    [Fact]
    public void The_Settings_pane_shows_a_delimiter_set_elsewhere()
    {
        var settings = Settings();
        var pane = new SettingsViewModel(settings);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)pane).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.CsvDelimiter = ";";

        Assert.True(pane.IsSemicolonDelimiter);
        Assert.Equal([nameof(SettingsViewModel.CsvDelimiter), nameof(SettingsViewModel.IsCommaDelimiter), nameof(SettingsViewModel.IsSemicolonDelimiter)], changed);

        changed.Clear();
        settings.CsvDelimiter = ";";
        Assert.Empty(changed);

        pane.IsCommaDelimiter = true;
        Assert.Equal(",", settings.CsvDelimiter);
        Assert.Contains(nameof(SettingsViewModel.IsSemicolonDelimiter), changed);
    }
}
