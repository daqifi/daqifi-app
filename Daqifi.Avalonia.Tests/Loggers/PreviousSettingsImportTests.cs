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
    /// <summary>Whether this user's profiles file records that the import ran.</summary>
    private bool ProfilesRecorded()
    {
        ProfileXmlStore.ReadWithoutRepair(CurrentProfiles, out var recorded);
        return recorded;
    }

    /// <summary>Whether a fresh start (new settings, new importer) would still offer the import.</summary>
    private bool OfferedAtNextLaunch() => Import().HasSettingsToImport(CurrentProfiles, Settings());

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

    private PreviousSettingsImport Import() => new(Old, _log);

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
        Assert.True(ProfilesRecorded());
        Assert.False(OfferedAtNextLaunch());
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
        Assert.False(OfferedAtNextLaunch());
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
        Settings();
        // A later launch: what marks the comma as the default must survive in the file.
        var settings = Settings();

        var outcome = await Import().ImportAsync(Manager(), settings);

        Assert.True(outcome.DelimiterCopied);
        Assert.Equal(";", settings.CsvDelimiter);
        Assert.Equal(";", Settings().CsvDelimiter);
        // No profiles file in the old folder: nothing to read, so nothing is left to offer.
        Assert.False(OfferedAtNextLaunch());
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
        File.SetLastWriteTimeUtc(CurrentSettings, longAgo);
        var settingsBefore = File.ReadAllBytes(CurrentSettings);

        var outcome = await Import().ImportAsync(manager, settings);

        // The settings file is not touched. The profiles file keeps its one profile, as it was,
        // and gains only the record that the import ran (#512 review round 3).
        Assert.Equal(0, outcome.ProfilesAdded);
        Assert.False(outcome.DelimiterCopied);
        Assert.Equal(settingsBefore, File.ReadAllBytes(CurrentSettings));
        Assert.Equal(longAgo, File.GetLastWriteTimeUtc(CurrentSettings));
        Assert.Equal("Mine", Assert.Single(Manager().SubscribedProfiles).Name);
        Assert.True(ProfilesRecorded());

        // Once the import is recorded, a later Import writes nothing at all.
        File.SetLastWriteTimeUtc(CurrentProfiles, longAgo);
        var before = Snapshot(Current);
        await Import().ImportAsync(manager, settings);
        AssertSameFiles(before, Snapshot(Current));
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

        var profilesLocked = lockedFile == PreviousSettingsImport.ProfilesFileName;
        Assert.Equal(!profilesLocked, ProfilesRecorded());
        Assert.True(OfferedAtNextLaunch());
        Assert.False(first.Complete);
        Assert.Contains(_log.Warnings, warning => warning.Contains("tries again"));

        var second = await Import().ImportAsync(manager, settings);

        // The file that could be read came across the first time; the locked one the second time.
        Assert.Equal(profilesLocked ? 0 : 1, first.ProfilesAdded);
        Assert.Equal(profilesLocked, first.DelimiterCopied);
        Assert.Equal(profilesLocked ? 1 : 0, second.ProfilesAdded);
        Assert.Equal(!profilesLocked, second.DelimiterCopied);
        Assert.Equal(";", settings.CsvDelimiter);
        Assert.True(second.Complete);
        Assert.True(ProfilesRecorded());
        Assert.False(OfferedAtNextLaunch());
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
        Assert.False(outcome.Complete);
        Assert.True(Import().HasSettingsToImport(manager.ProfilesFilePath, Settings()));
    }

    /// <summary>Off Windows, and under a <c>DAQIFI_DATA_DIR</c> override, there is nothing to read.</summary>
    [Fact]
    public async Task With_no_old_folder_to_read_the_import_does_nothing()
    {
        // This suite sets DAQIFI_DATA_DIR, and it is not Windows on CI either.
        Assert.Null(PreviousSettingsImport.ForThisPc().PreviousDirectory);
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        var manager = Manager();

        var none = new PreviousSettingsImport(null, _log);
        Assert.False(none.HasSettingsToImport(CurrentProfiles, Settings()));
        var outcome = await none.ImportAsync(manager, Settings());

        Assert.Equal(0, outcome.ProfilesAdded);
        Assert.Empty(manager.SubscribedProfiles);
        Assert.False(ProfilesRecorded());
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

    // ---- Review of PR #667 (2026-11-21). Each test below follows the reviewer's reproduction. ----

    /// <summary>
    /// Review P1. Another window saves a profile after the import read this user's file and before
    /// it wrote its merged copy. An atomic replace keeps the file whole, but without one lock for
    /// the read, the change and the write, the import writes its older copy over the other save.
    /// </summary>
    /// <remarks>
    /// The candidates are read inside the import's read-and-write, so a lazy sequence is where the
    /// other window's save can overlap. With no lock the other save finishes at once (a write of
    /// this file takes a few milliseconds). With the shared lock it waits for the import, so the
    /// wait here gives up after 50 ms, well inside the lock's own wait
    /// (<c>AppDataFile.WriteLockWait</c>), and the import goes on and releases the lock in time.
    /// </remarks>
    [Fact]
    public async Task A_profile_another_window_saves_during_the_import_is_kept()
    {
        var manager = Manager();
        var otherWindow = Manager();
        Task<bool>? otherSave = null;

        IEnumerable<Profile> CandidatesThatOverlapAnotherSave()
        {
            otherSave = Task.Run(() => otherWindow.SubscribeProfile(UsersProfile(FieldId, "Saved in the other window")));
            otherSave.Wait(TimeSpan.FromMilliseconds(50));
            yield return UsersProfile(BenchId, "Bench rig");
        }

        manager.AddProfilesNotYetSaved(CandidatesThatOverlapAnotherSave());

        Assert.True(await otherSave!);
        Assert.Equal(new[] { BenchId, FieldId }.Order(), Manager().SubscribedProfiles.Select(p => p.ProfileId).Order());
    }

    /// <summary>
    /// Review P2. Profiles and the delimiter are two separate files. When only the delimiter could
    /// not be read, the retry must not bring back a profile the user deleted in between.
    /// </summary>
    [Fact]
    public async Task A_retry_after_a_locked_settings_file_does_not_bring_back_a_deleted_profile()
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        WriteDelimiter(OldSettings, ";");
        var manager = Manager();
        var settings = Settings();
        using (new FileStream(OldSettings, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(1, (await Import().ImportAsync(manager, settings)).ProfilesAdded);
        }

        Assert.True(manager.UnsubscribeProfile(manager.SubscribedProfiles.Single()));

        var retry = await Import().ImportAsync(manager, settings);

        Assert.Equal(0, retry.ProfilesAdded);
        Assert.Empty(manager.SubscribedProfiles);
        Assert.True(retry.DelimiterCopied);
        Assert.Equal(";", settings.CsvDelimiter);
    }

    /// <summary>
    /// Review P2. When this user's settings file cannot be written, the delimiter did not come
    /// across: it is not reported as copied, it is not kept in memory (so a retry still writes it),
    /// and a retry in the same run copies it once the folder can be written.
    /// </summary>
    [Fact]
    public async Task A_delimiter_that_cannot_be_saved_is_not_reported_copied_and_is_tried_again()
    {
        WriteDelimiter(OldSettings, ";");
        var blocker = Path.Combine(_root, "settings-blocker");
        File.WriteAllText(blocker, "a file where a folder should be");
        var settingsPath = Path.Combine(blocker, PreviousSettingsImport.SettingsFileName);
        var settings = new DaqifiSettings(settingsPath);
        var manager = Manager();

        var first = await Import().ImportAsync(manager, settings);

        Assert.False(first.DelimiterCopied);
        Assert.Equal(",", settings.CsvDelimiter);

        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        var retry = await Import().ImportAsync(manager, settings);

        Assert.True(retry.DelimiterCopied);
        Assert.Equal(";", new DaqifiSettings(settingsPath).CsvDelimiter);
    }

    /// <summary>
    /// Review P2. The user selected a semicolon and then a comma again. That comma is a choice,
    /// even though it is the same text as the first-launch default, so the import keeps it.
    /// </summary>
    [Fact]
    public async Task A_comma_the_user_selected_before_the_import_is_kept()
    {
        var settings = Settings();
        settings.CsvDelimiter = ";";
        settings.CsvDelimiter = ",";
        WriteDelimiter(OldSettings, ";");

        var outcome = await Import().ImportAsync(Manager(), Settings());

        Assert.False(outcome.DelimiterCopied);
        Assert.Equal(",", Settings().CsvDelimiter);
    }

    /// <summary>
    /// Review P2, the other side of one marker per part: a part that is done does not run again.
    /// After a Dismiss nothing comes across, though the old folder still holds a profile and a
    /// semicolon, and this user's delimiter is still the first-launch default.
    /// </summary>
    [Fact]
    public async Task A_part_that_is_done_does_not_run_again()
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        WriteDelimiter(OldSettings, ";");
        var dismissedIn = Settings();
        Import().MarkDone(Manager(), dismissedIn);
        Assert.False(Import().HasSettingsToImport(CurrentProfiles, dismissedIn));

        // A later launch: the Dismiss must be in the files, not only in memory.
        var manager = Manager();
        var settings = Settings();

        Assert.False(Import().HasSettingsToImport(CurrentProfiles, settings));
        var outcome = await Import().ImportAsync(manager, settings);

        Assert.Equal(0, outcome.ProfilesAdded);
        Assert.False(outcome.DelimiterCopied);
        Assert.Empty(manager.SubscribedProfiles);
        Assert.Equal(",", settings.CsvDelimiter);
    }

    /// <summary>
    /// Review P1, the offer: the bell offers Import for profiles or a delimiter this user lacks,
    /// and for nothing else. Each row is one reason to offer or not to.
    /// </summary>
    [Theory]
    [InlineData("new profile", true)]
    [InlineData("profile already here", false)]
    [InlineData("new delimiter", true)]
    [InlineData("unsupported delimiter", false)]
    [InlineData("delimiter the user chose", false)]
    [InlineData("comma chosen in an earlier session", false)]
    [InlineData("comma in a file from an earlier build", false)]
    [InlineData("comma rewritten without a flag after launch", false)]
    [InlineData("settings file here busy", true)]
    [InlineData("comma chosen earlier, settings file busy", false)]
    [InlineData("comma from an earlier build, settings file busy", false)]
    [InlineData("dismissed in this run, settings file busy", false)]
    [InlineData("locked profiles file", true)]
    [InlineData("damaged profiles file", false)]
    [InlineData("locked settings file", true)]
    [InlineData("damaged settings file", false)]
    [InlineData("both parts done", false)]
    public void The_offer_is_made_only_for_something_this_user_lacks(string oldFolder, bool offered)
    {
        var manager = Manager();
        var settings = Settings();
        switch (oldFolder)
        {
            case "new profile":
                WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
                break;
            case "profile already here":
                Assert.True(manager.SubscribeProfile(UsersProfile(BenchId, "Mine")));
                WriteProfiles(OldProfiles, WpfProfile(BenchId, "Theirs"));
                WriteDelimiter(OldSettings, ",");
                break;
            case "new delimiter":
                WriteDelimiter(OldSettings, ";");
                break;
            case "unsupported delimiter":
                WriteDelimiter(OldSettings, "|");
                break;
            case "delimiter the user chose":
                settings.CsvDelimiter = ";";
                WriteDelimiter(OldSettings, ",");
                break;
            case "comma chosen in an earlier session":
                // Loaded from the file at this launch, so the offer depends on what the load read.
                File.WriteAllText(CurrentSettings, "<DAQifiSettings><CsvDelimiter chosenByUser=\"true\">,</CsvDelimiter></DAQifiSettings>");
                settings = Settings();
                WriteDelimiter(OldSettings, ";");
                break;
            case "comma in a file from an earlier build":
                File.WriteAllText(CurrentSettings, "<DAQifiSettings><CsvDelimiter>,</CsvDelimiter></DAQifiSettings>");
                settings = Settings();
                WriteDelimiter(OldSettings, ";");
                break;
            case "comma rewritten without a flag after launch":
                // This copy loaded the first-launch default; an older copy then wrote the file
                // without the flag. The file decides (#512 review round 4).
                File.WriteAllText(CurrentSettings, "<DAQifiSettings><CsvDelimiter>,</CsvDelimiter></DAQifiSettings>");
                WriteDelimiter(OldSettings, ";");
                break;
            case "settings file here busy":
                // The file cannot be read now, so the check cannot settle it: the Import can try.
                WriteDelimiter(OldSettings, ";");
                break;
            // The next three: the file is busy at the check, so what this copy loaded or did in
            // this run must settle the delimiter on its own.
            case "comma chosen earlier, settings file busy":
                File.WriteAllText(CurrentSettings, "<DAQifiSettings><CsvDelimiter chosenByUser=\"true\">,</CsvDelimiter></DAQifiSettings>");
                settings = Settings();
                WriteDelimiter(OldSettings, ";");
                break;
            case "comma from an earlier build, settings file busy":
                File.WriteAllText(CurrentSettings, "<DAQifiSettings><CsvDelimiter>,</CsvDelimiter></DAQifiSettings>");
                settings = Settings();
                WriteDelimiter(OldSettings, ";");
                break;
            case "dismissed in this run, settings file busy":
                WriteDelimiter(OldSettings, ";");
                Import().MarkDone(manager, settings);
                break;
            case "locked profiles file":
            case "damaged profiles file":
                File.WriteAllText(OldProfiles, "<Profiles><Profile><Name>Bench");
                break;
            case "locked settings file":
            case "damaged settings file":
                File.WriteAllText(OldSettings, "<DAQifiSettings><CsvDelimiter>;");
                break;
            case "both parts done":
                WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
                WriteDelimiter(OldSettings, ";");
                Import().MarkDone(manager, settings);
                break;
        }

        using var held = oldFolder switch
        {
            "locked profiles file" => new FileStream(OldProfiles, FileMode.Open, FileAccess.ReadWrite, FileShare.None),
            "locked settings file" => new FileStream(OldSettings, FileMode.Open, FileAccess.ReadWrite, FileShare.None),
            _ when oldFolder.EndsWith("settings file busy", StringComparison.Ordinal) || oldFolder == "settings file here busy"
                => new FileStream(CurrentSettings, FileMode.Open, FileAccess.ReadWrite, FileShare.None),
            _ => null,
        };

        Assert.Equal(offered, Import().HasSettingsToImport(CurrentProfiles, settings));
    }

    // ---- Review round 2 (2026-11-21, 01:07Z). Each test follows the reviewer's reproduction. ----

    /// <summary>
    /// Round 2, P2. Two copies of the app have the same settings file open. The second selects a
    /// semicolon and then a comma. The first copy's import must read that saved choice, not the
    /// one it cached at launch, and keep the comma.
    /// </summary>
    [Fact]
    public async Task A_comma_selected_in_another_running_copy_is_kept()
    {
        var first = Settings();
        var second = Settings();
        second.CsvDelimiter = ";";
        second.CsvDelimiter = ",";
        WriteDelimiter(OldSettings, ";");

        var outcome = await Import().ImportAsync(Manager(), first);

        Assert.False(outcome.DelimiterCopied);
        Assert.True(outcome.Complete);
        Assert.Equal(",", Settings().CsvDelimiter);
    }

    /// <summary>
    /// Round 2, P2, the import's side of the same lock: while another writer holds it, the import
    /// does not write the settings file and does not count the delimiter as done. When the lock
    /// is gone, a retry copies it.
    /// </summary>
    [Fact]
    public async Task An_import_does_not_write_the_settings_while_another_writer_holds_the_lock()
    {
        WriteDelimiter(OldSettings, ";");
        var settings = Settings();

        SettingsImportOutcome first;
        using (new FileStream(CurrentSettings + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            first = await Import().ImportAsync(Manager(), settings);
        }

        Assert.False(first.DelimiterCopied);
        Assert.False(first.Complete);
        Assert.Equal(",", Settings().CsvDelimiter);

        Assert.True((await Import().ImportAsync(Manager(), settings)).DelimiterCopied);
        Assert.Equal(";", Settings().CsvDelimiter);
    }

    /// <summary>
    /// Round 2, P2. <see cref="DaqifiSettings.ImportCsvDelimiter"/> decides from what the file says
    /// at that moment, whatever this copy loaded. Only "not chosen" (or no file) may be replaced; a
    /// file with no flag, such as one an older copy of the app has just written, is kept.
    /// </summary>
    [Theory]
    [InlineData("<DAQifiSettings><CsvDelimiter chosenByUser=\"false\">,</CsvDelimiter></DAQifiSettings>", true)]
    [InlineData(null, true)]
    [InlineData("<DAQifiSettings><CsvDelimiter chosenByUser=\"true\">,</CsvDelimiter></DAQifiSettings>", false)]
    [InlineData("<DAQifiSettings><CsvDelimiter>,</CsvDelimiter></DAQifiSettings>", false)]
    public void The_import_replaces_the_delimiter_only_while_the_file_says_it_is_the_default(string? fileNow, bool replaced)
    {
        var settings = Settings();
        if (fileNow is null)
        {
            File.Delete(CurrentSettings);
        }
        else
        {
            File.WriteAllText(CurrentSettings, fileNow);
        }

        var result = settings.ImportCsvDelimiter(";");

        Assert.Equal(replaced ? DaqifiSettings.DelimiterImportResult.Copied : DaqifiSettings.DelimiterImportResult.KeptChosen, result);
        Assert.Equal(replaced ? ";" : ",", Settings().CsvDelimiter);
    }

    /// <summary>
    /// Round 2, P2. An ordinary settings save takes the same lock as the import, so it never
    /// writes between the import's read and its write. While another writer holds the lock, the
    /// save does not reach the file.
    /// </summary>
    [Fact]
    public void An_ordinary_settings_save_does_not_write_while_another_writer_holds_the_lock()
    {
        var settings = Settings();

        using (new FileStream(CurrentSettings + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            settings.CsvDelimiter = ";";
        }

        Assert.Equal(",", Settings().CsvDelimiter);
    }

    /// <summary>
    /// Round 2, P2. This user's profiles file is busy, there is an old semicolon, and there is no
    /// old profiles file. The busy file must not hide the delimiter from the offer. And when there
    /// are old profiles, a busy file means "cannot tell", so the Import is offered and can try.
    /// </summary>
    /// <remarks>The last row: with nothing old to bring, a busy file must not cause an offer either.</remarks>
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    public void A_busy_profiles_file_here_does_not_hide_the_offer(bool oldProfiles, bool oldSemicolon, bool offered)
    {
        var manager = Manager();
        var settings = Settings();
        if (oldProfiles)
        {
            WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        }

        if (oldSemicolon)
        {
            WriteDelimiter(OldSettings, ";");
        }

        using var busy = new FileStream(CurrentProfiles, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(offered, Import().HasSettingsToImport(CurrentProfiles, settings));
    }

    /// <summary>
    /// Round 2, P2, as the round-3 design meets it. The record of the profiles part cannot be
    /// written: here because another writer holds the profiles lock, so the one write that holds
    /// both the profiles and their record fails. Nothing is added, nothing is recorded, and the
    /// import is not complete. Later the profile comes across with its record, and a profile the
    /// user then deletes stays deleted.
    /// </summary>
    [Fact]
    public async Task A_profiles_record_that_cannot_be_written_stops_the_part_until_it_can_be()
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        var manager = Manager();
        var settings = Settings();

        SettingsImportOutcome first;
        using (new FileStream(CurrentProfiles + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            first = await Import().ImportAsync(manager, settings);
        }

        Assert.False(first.Complete);
        Assert.Equal(0, first.ProfilesAdded);
        Assert.Empty(manager.SubscribedProfiles);
        Assert.False(ProfilesRecorded());

        var second = await Import().ImportAsync(manager, settings);
        Assert.True(second.Complete);
        Assert.Equal(1, second.ProfilesAdded);

        Assert.True(manager.UnsubscribeProfile(manager.SubscribedProfiles.Single()));
        var third = await Import().ImportAsync(manager, settings);
        Assert.Equal(0, third.ProfilesAdded);
        Assert.Empty(manager.SubscribedProfiles);
    }

    // ---- Review round 3 (2026-11-21, 01:37Z). Each test follows the reviewer's reproduction. ----

    /// <summary>
    /// Round 3, P1, the reviewer's state. A run of the round-2 design stopped after it wrote a
    /// part's marker file and before it saved that part. A fresh start must still offer and
    /// import the part: completion must not be a separate record that can exist without the work.
    /// </summary>
    [Theory]
    [InlineData(".previous-settings-import-profiles-done")]
    [InlineData(".previous-settings-import-delimiter-done")]
    public async Task A_record_left_by_a_run_that_stopped_before_its_save_does_not_stop_the_import(string leftOver)
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        WriteDelimiter(OldSettings, ";");
        File.WriteAllText(Path.Combine(Current, leftOver), string.Empty);
        var manager = Manager();
        var settings = Settings();

        Assert.True(Import().HasSettingsToImport(CurrentProfiles, settings));
        var outcome = await Import().ImportAsync(manager, settings);

        Assert.Equal(1, outcome.ProfilesAdded);
        Assert.True(outcome.DelimiterCopied);
        Assert.Equal("Bench rig", Assert.Single(Manager().SubscribedProfiles).Name);
        Assert.Equal(";", Settings().CsvDelimiter);
    }

    /// <summary>
    /// Round 3, P1, the new design at every step. A run is cut off at each step (an exception from
    /// the step hook stands for a closed app or a power cut there). A fresh start (new manager,
    /// settings and importer, as after a restart) must find each part either done with its record
    /// or not done, with no record, and still offered: never recorded without the work. A retry
    /// then finishes, and a profile the user deletes after that stays deleted and is not offered.
    /// </summary>
    /// <remarks>
    /// Cut off inside the profiles write, the write is abandoned before the file is replaced, as a
    /// power cut would leave it. The run then goes on to the delimiter, which a real cut would not
    /// do; this test checks only what that leaves on disk.
    /// </remarks>
    [Theory]
    [InlineData(nameof(PreviousSettingsImport.Step.ProfilesRead))]
    [InlineData(nameof(PreviousSettingsImport.Step.ProfilesWriting))]
    [InlineData(nameof(PreviousSettingsImport.Step.ProfilesWritten))]
    [InlineData(nameof(PreviousSettingsImport.Step.DelimiterRead))]
    [InlineData(nameof(PreviousSettingsImport.Step.DelimiterWritten))]
    public async Task A_run_cut_off_at_any_step_leaves_each_part_done_or_offered_and_the_next_Import_finishes(string cutAt)
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        WriteDelimiter(OldSettings, ";");
        var step = Enum.Parse<PreviousSettingsImport.Step>(cutAt);
        var cutOff = new PreviousSettingsImport(Old, _log)
        {
            AtStep = reached =>
            {
                if (reached == step)
                {
                    throw new IOException("Stands for a closed app or a power cut.");
                }
            },
        };

        await cutOff.ImportAsync(Manager(), Settings());

        // After a restart: the profile is here exactly when its record is, and the delimiter is
        // the old one exactly when the file says it was chosen. Whatever is missing is offered.
        var profileHere = Manager().SubscribedProfiles.Any(p => p.ProfileId == BenchId);
        Assert.Equal(profileHere, ProfilesRecorded());
        var stored = DaqifiSettings.ReadStoredDelimiter(CurrentSettings);
        var delimiterHere = stored?.Value == ";";
        Assert.Equal(delimiterHere, stored?.ChosenByUser == true);
        Assert.Equal(!(profileHere && delimiterHere), OfferedAtNextLaunch());

        var retry = await Import().ImportAsync(Manager(), Settings());

        Assert.True(retry.Complete);
        var manager = Manager();
        Assert.Equal("Bench rig", Assert.Single(manager.SubscribedProfiles).Name);
        Assert.Equal(";", Settings().CsvDelimiter);
        Assert.False(OfferedAtNextLaunch());

        Assert.True(manager.UnsubscribeProfile(manager.SubscribedProfiles.Single()));
        Assert.False(OfferedAtNextLaunch());
        Assert.Equal(0, (await Import().ImportAsync(Manager(), Settings())).ProfilesAdded);
        Assert.Empty(Manager().SubscribedProfiles);
    }

    private const string NeedsEnforcedPermissions =
        "Needs a folder the current user truly cannot read: not on Windows, and not as root.";

    /// <summary>
    /// Round 3, P2. The old folder was readable when the offer was shown, and then access went
    /// away. A file the process cannot open is not an absent file: the Import copies nothing,
    /// says it is not complete, and keeps the offer. When access comes back, both parts come
    /// across.
    /// </summary>
    [EnforcedReadOnlyDirectoryFact(NeedsEnforcedPermissions)]
    public async Task An_old_folder_that_cannot_be_read_is_not_taken_for_an_empty_one()
    {
        if (OperatingSystem.IsWindows()) { Assert.Fail(NeedsEnforcedPermissions); return; }
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        WriteDelimiter(OldSettings, ";");
        var manager = Manager();
        var settings = Settings();
        Assert.True(Import().HasSettingsToImport(CurrentProfiles, settings));

        SettingsImportOutcome blocked;
        File.SetUnixFileMode(Old, UnixFileMode.None);
        try
        {
            blocked = await Import().ImportAsync(manager, settings);
        }
        finally
        {
            File.SetUnixFileMode(Old, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.False(blocked.Complete);
        Assert.Equal(0, blocked.ProfilesAdded);
        Assert.False(blocked.DelimiterCopied);
        Assert.True(Import().HasSettingsToImport(CurrentProfiles, settings));

        var later = await Import().ImportAsync(manager, settings);
        Assert.Equal(1, later.ProfilesAdded);
        Assert.True(later.DelimiterCopied);
    }

    /// <summary>
    /// Round 3, P2, one file at a time. Only the old settings file cannot be read; the profiles
    /// come across. The delimiter part must not count as done: the import is not complete, and the
    /// next launch still offers it. When the file can be read, the delimiter comes across.
    /// </summary>
    [EnforcedReadOnlyDirectoryFact(NeedsEnforcedPermissions)]
    public async Task An_old_settings_file_that_cannot_be_read_is_not_taken_for_an_absent_one()
    {
        if (OperatingSystem.IsWindows()) { Assert.Fail(NeedsEnforcedPermissions); return; }
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        WriteDelimiter(OldSettings, ";");
        var manager = Manager();
        var settings = Settings();

        SettingsImportOutcome blocked;
        bool offeredWhileBlocked;
        File.SetUnixFileMode(OldSettings, UnixFileMode.None);
        try
        {
            blocked = await Import().ImportAsync(manager, settings);
            offeredWhileBlocked = OfferedAtNextLaunch();
        }
        finally
        {
            File.SetUnixFileMode(OldSettings, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal(1, blocked.ProfilesAdded);
        Assert.False(blocked.Complete);
        Assert.True(offeredWhileBlocked);
        Assert.True((await Import().ImportAsync(manager, settings)).DelimiterCopied);
    }

    /// <summary>
    /// Round 3: Dismiss now writes its record into the settings file, so it takes the settings
    /// writers' lock like every other writer. While another writer holds it, Dismiss does not
    /// write, and the delimiter is still offered at the next launch.
    /// </summary>
    [Fact]
    public void Dismiss_does_not_write_the_settings_while_another_writer_holds_the_lock()
    {
        WriteDelimiter(OldSettings, ";");
        var settings = Settings();

        using (new FileStream(CurrentSettings + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(settings.KeepCsvDelimiter());
        }

        Assert.Equal(false, DaqifiSettings.ReadStoredDelimiter(CurrentSettings)?.ChosenByUser);
        Assert.True(OfferedAtNextLaunch());
    }

    // ---- Review round 4 (2026-11-21, 02:06Z). Each test follows the reviewer's reproduction. ----

    /// <summary>
    /// Round 4, P2. A part that is finished (imported, or dismissed) is finished for good, even
    /// when its old file is locked later: the record in this user's file ends it before the old
    /// file is read. The next launch offers nothing, and a retry is complete.
    /// </summary>
    [Theory]
    [InlineData("imported", PreviousSettingsImport.ProfilesFileName)]
    [InlineData("imported", PreviousSettingsImport.SettingsFileName)]
    [InlineData("dismissed", PreviousSettingsImport.ProfilesFileName)]
    [InlineData("dismissed", PreviousSettingsImport.SettingsFileName)]
    public async Task A_finished_part_stays_finished_when_its_old_file_is_locked_later(string finishedBy, string lockedLater)
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        WriteDelimiter(OldSettings, ";");
        if (finishedBy == "imported")
        {
            Assert.True((await Import().ImportAsync(Manager(), Settings())).Complete);
        }
        else
        {
            Import().MarkDone(Manager(), Settings());
        }

        Assert.False(OfferedAtNextLaunch());

        bool offered;
        SettingsImportOutcome retry;
        using (new FileStream(Path.Combine(Old, lockedLater), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            offered = OfferedAtNextLaunch();
            retry = await Import().ImportAsync(Manager(), Settings());
        }

        Assert.False(offered);
        Assert.True(retry.Complete);
    }

    /// <summary>
    /// Round 4, P2. An old folder with no profiles file and an old semicolon. There is nothing to
    /// copy into this user's profiles file, so the import does not touch it (not even its lock,
    /// held here by another writer). The delimiter comes across and the import is complete.
    /// </summary>
    [Fact]
    public async Task A_delimiter_only_import_does_not_touch_the_profiles_file()
    {
        WriteDelimiter(OldSettings, ";");
        var manager = Manager();
        var settings = Settings();
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(CurrentProfiles, longAgo);
        var profilesBefore = File.ReadAllBytes(CurrentProfiles);

        SettingsImportOutcome outcome;
        using (new FileStream(CurrentProfiles + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            outcome = await Import().ImportAsync(manager, settings);
        }

        Assert.True(outcome.DelimiterCopied);
        Assert.True(outcome.Complete);
        Assert.Equal(";", Settings().CsvDelimiter);
        Assert.Equal(profilesBefore, File.ReadAllBytes(CurrentProfiles));
        Assert.Equal(longAgo, File.GetLastWriteTimeUtc(CurrentProfiles));
    }

    /// <summary>
    /// Round 4. A delimiter chosen in Settings whose save failed (another writer held the lock)
    /// is still the user's choice in this copy, so the import keeps it although the file still
    /// says "first-launch default".
    /// </summary>
    [Fact]
    public async Task A_choice_whose_save_failed_is_still_kept_by_the_import()
    {
        WriteDelimiter(OldSettings, ",");
        var settings = Settings();
        using (new FileStream(CurrentSettings + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            settings.CsvDelimiter = ";";
        }

        var outcome = await Import().ImportAsync(Manager(), settings);

        Assert.False(outcome.DelimiterCopied);
        Assert.Equal(";", settings.CsvDelimiter);
    }

    /// <summary>
    /// Round 4. The record check reads this user's profiles file. When that file is busy, the
    /// check cannot say "finished", so the part is not taken as finished: the import is not
    /// complete, and a retry brings the profile over.
    /// </summary>
    [Fact]
    public async Task A_busy_profiles_file_here_does_not_pass_for_a_finished_part()
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        var manager = Manager();
        var settings = Settings();

        SettingsImportOutcome busy;
        using (new FileStream(CurrentProfiles, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            busy = await Import().ImportAsync(manager, settings);
        }

        Assert.False(busy.Complete);
        Assert.Equal(1, (await Import().ImportAsync(manager, settings)).ProfilesAdded);
    }

    /// <summary>
    /// Round 4. The record check runs before the write, so another window can record the import
    /// (a Dismiss there, say) in between. The write itself checks again under the lock: it adds
    /// nothing, and it does not even rewrite the file.
    /// </summary>
    [Fact]
    public async Task A_record_another_window_writes_during_the_import_is_respected()
    {
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var racing = new PreviousSettingsImport(Old, _log)
        {
            AtStep = reached =>
            {
                if (reached == PreviousSettingsImport.Step.ProfilesRead)
                {
                    Assert.True(Manager().RecordEarlierVersionImport());
                    File.SetLastWriteTimeUtc(CurrentProfiles, longAgo);
                }
            },
        };

        var outcome = await racing.ImportAsync(Manager(), Settings());

        // The time first: a new manager's startup load writes the file, as the app's does.
        Assert.Equal(longAgo, File.GetLastWriteTimeUtc(CurrentProfiles));
        Assert.Equal(0, outcome.ProfilesAdded);
        Assert.True(outcome.Complete);
        Assert.Empty(Manager().SubscribedProfiles);
    }

    /// <summary>
    /// Round 4, the same race for the delimiter: another copy saves a choice after the settled
    /// check. The locked write reads the file again and keeps it, and that part is finished.
    /// </summary>
    [Fact]
    public async Task A_choice_another_copy_saves_during_the_import_is_kept()
    {
        WriteDelimiter(OldSettings, ";");
        var racing = new PreviousSettingsImport(Old, _log)
        {
            AtStep = reached =>
            {
                if (reached == PreviousSettingsImport.Step.DelimiterRead)
                {
                    var otherCopy = Settings();
                    otherCopy.CsvDelimiter = ";";
                    otherCopy.CsvDelimiter = ",";
                }
            },
        };

        var outcome = await racing.ImportAsync(Manager(), Settings());

        Assert.False(outcome.DelimiterCopied);
        Assert.True(outcome.Complete);
        Assert.Equal(",", Settings().CsvDelimiter);
    }

    /// <summary>
    /// Round 3, P2, one file at a time, for the profiles. Only the old profiles file cannot be
    /// read; the delimiter comes across. The profiles part must not pass for one with nothing to
    /// copy: the import is not complete, and the next launch still offers it.
    /// </summary>
    [EnforcedReadOnlyDirectoryFact(NeedsEnforcedPermissions)]
    public async Task An_old_profiles_file_that_cannot_be_read_is_not_taken_for_an_absent_one()
    {
        if (OperatingSystem.IsWindows()) { Assert.Fail(NeedsEnforcedPermissions); return; }
        WriteProfiles(OldProfiles, WpfProfile(BenchId, "Bench rig"));
        WriteDelimiter(OldSettings, ";");
        var manager = Manager();
        var settings = Settings();

        SettingsImportOutcome blocked;
        bool offeredWhileBlocked;
        File.SetUnixFileMode(OldProfiles, UnixFileMode.None);
        try
        {
            blocked = await Import().ImportAsync(manager, settings);
            offeredWhileBlocked = OfferedAtNextLaunch();
        }
        finally
        {
            File.SetUnixFileMode(OldProfiles, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.True(blocked.DelimiterCopied);
        Assert.False(blocked.Complete);
        Assert.True(offeredWhileBlocked);
        Assert.Equal(1, (await Import().ImportAsync(manager, settings)).ProfilesAdded);
    }

    // ---- Review round 5 (2026-11-21, 02:53Z). The test follows the reviewer's reproduction. ----

    /// <summary>
    /// Round 5, P2. The import has made its early check and is reading the old file (a comma).
    /// Meanwhile the user selects a semicolon in THIS window while another writer holds the
    /// settings lock, so the choice is only in memory and the file still says "first-launch
    /// default". When the import goes on, the copy must still see this window's choice and keep
    /// it, not put the comma back.
    /// </summary>
    [Fact]
    public async Task A_choice_made_in_this_window_during_the_import_is_kept_although_its_save_failed()
    {
        WriteDelimiter(OldSettings, ",");
        var settings = Settings();
        var racing = new PreviousSettingsImport(Old, _log)
        {
            AtStep = reached =>
            {
                if (reached == PreviousSettingsImport.Step.DelimiterRead)
                {
                    using var otherWriter = new FileStream(CurrentSettings + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    settings.CsvDelimiter = ";";
                }
            },
        };

        var outcome = await racing.ImportAsync(Manager(), settings);

        Assert.False(outcome.DelimiterCopied);
        Assert.True(outcome.Complete);
        Assert.Equal(";", settings.CsvDelimiter);
        Assert.Equal(false, DaqifiSettings.ReadStoredDelimiter(CurrentSettings)?.ChosenByUser);
    }

    /// <summary>
    /// Review P2, the conservative rule. A settings file that an earlier build wrote does not say
    /// whether its comma was chosen, so the import keeps it rather than guess.
    /// </summary>
    [Fact]
    public async Task A_comma_in_a_settings_file_from_an_earlier_build_is_kept()
    {
        File.WriteAllText(CurrentSettings, "<DAQifiSettings><CsvDelimiter>,</CsvDelimiter></DAQifiSettings>");
        WriteDelimiter(OldSettings, ";");

        var outcome = await Import().ImportAsync(Manager(), Settings());

        Assert.False(outcome.DelimiterCopied);
        Assert.Equal(",", Settings().CsvDelimiter);
    }
}
