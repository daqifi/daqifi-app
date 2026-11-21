using System.Runtime.Versioning;
using System.Text;
using Daqifi.Avalonia.Services;
using Daqifi.Avalonia.Views.Mobile;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Exporter;
using Daqifi.Desktop.Logger;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Exporter;

/// <summary>
/// Pins the mobile export to a storage item that has NO file-system path (issue #587).
///
/// <para>
/// The regression: on Android every save or folder pick is a <c>content://</c> URI. The mobile
/// Storage tab converted the pick with <c>TryGetLocalPath()</c>, got null, read the null as "the
/// user cancelled", and returned without a message — while the picker had already created an empty
/// destination file. Export and Export all wrote no data on Android at all.
/// </para>
///
/// <para>
/// The storage items here are in-memory doubles of a <c>content://</c> document: they have no path
/// and are reachable only through their streams, exactly as on the phone. (They implement the
/// writer's seam, because Avalonia 12 does not let user code implement <c>IStorageFile</c>.) The
/// CSV comes from the real <see cref="OptimizedLoggingSessionExporter"/> over a real SQLite
/// database, so a passing test means real rows reached the stream.
/// </para>
/// </summary>
public sealed class StorageExportWriterTests : IDisposable
{
    private const string PriorContent = "Time,Nyquist:SERIAL-0:AI9\n2020-01-01T00:00:00Z,42\n";

    private readonly string _root;
    private readonly string _staging;
    private readonly IDbContextFactory<LoggingContext> _contexts;
    private readonly RecordingAppLogger _logger = new();
    private readonly List<string> _lockedDirectories = [];
    private int _nextSessionId;

    public StorageExportWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "daqifi-storage-export-" + Guid.NewGuid().ToString("N"));
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_staging);
        _contexts = TestDatabase.Contexts(Path.Combine(_root, "DAQiFiDatabase.db"));
        using var context = _contexts.CreateDbContext();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        // A 0555 directory accepts no deletions from its own entries: writable again first.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var directory in _lockedDirectories)
            {
                try { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch (IOException) { /* best effort, as below */ }
            }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort — a leftover temp directory must not fail a test */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>The reported bug: a pick with no path must still receive the whole CSV.</summary>
    [Fact]
    public async Task A_content_uri_file_receives_the_complete_csv()
    {
        var session = SeedSession("Run", "AI0", 1.25);
        var file = MemoryStorageFile.PickerCreated("Run.csv");

        var wrote = await Writer().WriteAsync(file, ExportTo(session));

        Assert.True(wrote);
        Assert.False(file.Deleted);
        Assert.Equal(ExportByPath(session), file.Bytes);
        var lines = file.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + one row per timestamp
        Assert.Contains("AI0", lines[0]);
        AssertNoStagingLeft();
    }

    /// <summary>Nothing to export: the empty file the picker created must not stay behind.</summary>
    [Fact]
    public async Task Nothing_to_export_removes_the_empty_picked_file()
    {
        var file = MemoryStorageFile.PickerCreated("Empty.csv");

        var wrote = await Writer().WriteAsync(file, _ => false);

        Assert.False(wrote);
        Assert.True(file.Deleted);
        AssertNoStagingLeft();
    }

    /// <summary>The exporter fails: no 0-byte file, and the failure is logged.</summary>
    [Fact]
    public async Task An_export_failure_removes_the_empty_picked_file_and_logs()
    {
        var file = MemoryStorageFile.PickerCreated("Run.csv");

        var wrote = await Writer().WriteAsync(file, _ => throw new IOException("database gone"));

        Assert.False(wrote);
        Assert.True(file.Deleted);
        Assert.Contains(_logger.Errors, e => e.Contains("Run.csv", StringComparison.Ordinal));
        AssertNoStagingLeft();
    }

    /// <summary>An export failure never touches a prior, non-empty destination (the #236 promise).</summary>
    [Fact]
    public async Task An_export_failure_leaves_a_prior_file_untouched()
    {
        var file = MemoryStorageFile.Existing("Run.csv", PriorContent);

        var wrote = await Writer().WriteAsync(file, _ => throw new IOException("database gone"));

        Assert.False(wrote);
        Assert.False(file.Deleted);
        Assert.Equal(PriorContent, file.Text);
        Assert.Equal(0, file.OpenWriteCount);
    }

    /// <summary>The destination cannot be opened: the empty picked file is removed.</summary>
    [Fact]
    public async Task An_unwritable_destination_removes_the_empty_picked_file()
    {
        var session = SeedSession("Run", "AI0", 1.25);
        var file = MemoryStorageFile.PickerCreated("Run.csv");
        file.FailOpenWrite = true;

        var wrote = await Writer().WriteAsync(file, ExportTo(session));

        Assert.False(wrote);
        Assert.True(file.Deleted);
        AssertNoStagingLeft();
    }

    /// <summary>The copy fails part-way: a partial CSV looks complete, so it is removed.</summary>
    [Fact]
    public async Task A_failure_during_the_copy_removes_the_partial_file()
    {
        var session = SeedSession("Run", "AI0", 1.25);
        var file = MemoryStorageFile.Existing("Run.csv", PriorContent);
        file.FailAfterBytes = 10;

        var wrote = await Writer().WriteAsync(file, ExportTo(session));

        Assert.False(wrote);
        Assert.True(file.Deleted);
        AssertNoStagingLeft();
    }

    /// <summary>A provider that opens without truncating must not keep the tail of a longer prior file.</summary>
    [Fact]
    public async Task A_non_truncating_stream_does_not_keep_the_tail_of_a_longer_prior_file()
    {
        var session = SeedSession("Run", "AI0", 1.25);
        var file = MemoryStorageFile.Existing("Run.csv", new string('x', 10_000));
        file.TruncateOnOpen = false;

        Assert.True(await Writer().WriteAsync(file, ExportTo(session)));

        Assert.Equal(ExportByPath(session), file.Bytes);
    }

    /// <summary>Export all: each session becomes its own complete file in a content-URI folder, and a
    /// file already in the folder keeps its name and its bytes — the export takes the next free name
    /// instead of creating over it (CreateFileAsync truncates).</summary>
    [Fact]
    public async Task Export_all_creates_one_complete_file_per_session_and_never_over_an_existing_one()
    {
        var first = SeedSession("Run", "AI0", 1.25);
        var second = SeedSession("Run", "AI1", 7.5);
        var folder = new MemoryStorageFolder("Documents");
        folder.Seed("Run.csv", PriorContent);
        var namer = new ExportFileNamer();
        var writer = Writer();

        var a = await writer.WriteNewAsync(folder, namer.NextCsvName(first.Name, first.ID), ExportTo(first));
        var b = await writer.WriteNewAsync(folder, namer.NextCsvName(second.Name, second.ID), ExportTo(second));

        Assert.Equal("Run (2).csv", a); // "Run.csv" is taken
        Assert.Equal("Run (3).csv", b); // the namer said "Run (2).csv", which the first one now holds
        Assert.Equal(ExportByPath(first), folder.Files["Run (2).csv"].Bytes);
        Assert.Equal(ExportByPath(second), folder.Files["Run (3).csv"].Bytes);
        Assert.Equal(PriorContent, folder.Files["Run.csv"].Text);
        AssertNoStagingLeft();
    }

    /// <summary>The review case: a name collision followed by a failed copy. The previous export must
    /// survive byte for byte, and only the file this export created is removed.</summary>
    [Fact]
    public async Task Export_all_keeps_an_existing_file_when_the_copy_into_its_namesake_fails()
    {
        var session = SeedSession("Run", "AI0", 1.25);
        var folder = new MemoryStorageFolder("Documents") { FailNewFilesAfterBytes = 10 };
        folder.Seed("Run.csv", PriorContent);

        var name = await Writer().WriteNewAsync(folder, "Run.csv", ExportTo(session));

        Assert.Null(name);
        Assert.Equal(PriorContent, folder.Files["Run.csv"].Text);
        Assert.False(folder.Files["Run.csv"].Deleted);
        Assert.True(folder.Files["Run (2).csv"].Deleted);
        AssertNoStagingLeft();
    }

    /// <summary>The second review case (review of PR #603): a file appears in the folder AFTER the
    /// name check and before the create — cloud sync, say. It must keep its bytes, and the export
    /// must land under a fresh name instead of creating over it.</summary>
    [Fact]
    public async Task Export_all_never_replaces_a_file_that_appears_after_the_name_check()
    {
        var session = SeedSession("Run", "AI0", 1.25);
        var folder = new MemoryStorageFolder("Documents");
        folder.AppearBeforeCreate("Run.csv", PriorContent);

        var name = await Writer().WriteNewAsync(folder, "Run.csv", ExportTo(session));

        Assert.Equal("Run (2).csv", name);
        Assert.Equal(PriorContent, folder.Files["Run.csv"].Text);
        Assert.False(folder.Files["Run.csv"].Deleted);
        Assert.Equal(ExportByPath(session), folder.Files["Run (2).csv"].Bytes);
        AssertNoStagingLeft();
    }

    /// <summary>As above, with a copy that fails: the cleanup removes only the file this export
    /// created, never the one that appeared under the checked name.</summary>
    [Fact]
    public async Task Export_all_does_not_delete_a_file_that_appears_after_the_name_check_when_the_copy_fails()
    {
        var session = SeedSession("Run", "AI0", 1.25);
        var folder = new MemoryStorageFolder("Documents") { FailNewFilesAfterBytes = 10 };
        folder.AppearBeforeCreate("Run.csv", PriorContent);

        var name = await Writer().WriteNewAsync(folder, "Run.csv", ExportTo(session));

        Assert.Null(name);
        Assert.Equal(PriorContent, folder.Files["Run.csv"].Text);
        Assert.False(folder.Files["Run.csv"].Deleted);
        Assert.True(folder.Files["Run (2).csv"].Deleted);
        AssertNoStagingLeft();
    }

    /// <summary>The create a folder with a local path uses (the desktop, an in-sandbox iOS folder),
    /// against a real directory: it never replaces a file or steps onto a folder, and it creates an
    /// empty file only under a free name.</summary>
    [Fact]
    public void The_local_create_never_replaces_an_existing_file()
    {
        var directory = Path.Combine(_root, "picked");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Run.csv"), PriorContent);
        Directory.CreateDirectory(Path.Combine(directory, "Sub.csv"));

        Assert.False(StorageExportWriter.TryCreateNewLocalFile(directory, "Run.csv"));
        Assert.False(StorageExportWriter.TryCreateNewLocalFile(directory, "Sub.csv"));
        Assert.True(StorageExportWriter.TryCreateNewLocalFile(directory, "Run (2).csv"));

        Assert.Equal(PriorContent, File.ReadAllText(Path.Combine(directory, "Run.csv")));
        Assert.Equal(0, new FileInfo(Path.Combine(directory, "Run (2).csv")).Length);
    }

    /// <summary>
    /// Review of PR #685: a session whose name fills the 255-byte limit, exported into the same
    /// folder again. The first file takes the whole limit, so the " (2)" for the second one must
    /// come out of the name, not be added after it — a 259-byte name made the real create throw
    /// <see cref="PathTooLongException"/>, and Export All skipped a session that had exported fine
    /// the first time. Through the production create (<see cref="StorageExportWriter.TryCreateNewLocalFile"/>)
    /// on a real directory, so the file system itself is what accepts or refuses each name.
    /// </summary>
    [Fact]
    public async Task Exporting_a_longest_name_again_into_one_folder_gives_a_new_file_that_fits()
    {
        var session = SeedSession(new string('x', 300), "AI0", 1.25);
        var directory = Path.Combine(_root, "picked");
        Directory.CreateDirectory(directory);
        var folder = new LocalDirectoryFolder(directory);
        var writer = Writer();

        // A new namer each time, as each Export All tap makes one.
        var first = await writer.WriteNewAsync(folder, new ExportFileNamer().NextCsvName(session.Name, session.ID), ExportTo(session));
        var second = await writer.WriteNewAsync(folder, new ExportFileNamer().NextCsvName(session.Name, session.ID), ExportTo(session));
        var third = await writer.WriteNewAsync(folder, new ExportFileNamer().NextCsvName(session.Name, session.ID), ExportTo(session));

        Assert.Equal(new string('x', 251) + ".csv", first);
        Assert.Equal(new string('x', 247) + " (2).csv", second);
        Assert.Equal(new string('x', 247) + " (3).csv", third);
        Assert.All(new[] { first!, second!, third! }, name =>
            Assert.Equal(ExportByPath(session), File.ReadAllBytes(Path.Combine(directory, name))));
        Assert.Empty(_logger.Errors);
        AssertNoStagingLeft();
    }

    /// <summary>
    /// As above, for a name the namer already suffixed because two sessions in one Export All shared
    /// it: the count goes on from that suffix ("(3)"), and the name still fits.
    /// </summary>
    [Fact]
    public async Task Exporting_a_suffixed_longest_name_again_continues_the_count_and_fits()
    {
        var first = SeedSession(new string('x', 300), "AI0", 1.25);
        var second = SeedSession(new string('x', 300), "AI1", 2.5);
        var directory = Path.Combine(_root, "picked");
        Directory.CreateDirectory(directory);
        var folder = new LocalDirectoryFolder(directory);
        var writer = Writer();

        for (var run = 0; run < 2; run++)
        {
            var namer = new ExportFileNamer();
            Assert.NotNull(await writer.WriteNewAsync(folder, namer.NextCsvName(first.Name, first.ID), ExportTo(first)));
            Assert.NotNull(await writer.WriteNewAsync(folder, namer.NextCsvName(second.Name, second.ID), ExportTo(second)));
        }

        var names = Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                new string('x', 247) + " (2).csv",
                new string('x', 247) + " (3).csv",
                new string('x', 247) + " (4).csv",
                new string('x', 251) + ".csv",
            },
            names);
        Assert.All(names, name => Assert.True(Encoding.UTF8.GetByteCount(name!) <= 255, name));
        Assert.Empty(_logger.Errors);
    }

    /// <summary>
    /// The edge of the same case: a name that is one text element — 36 emoji joined by zero-width
    /// joiners, 249 bytes — fits on its own but leaves no room for " (2)". The second file falls
    /// back to a plain stem rather than to a bare " (2).csv", and still fits.
    /// </summary>
    /// <remarks>
    /// Not a letter with a long run of combining accents, which is the shape the namer's own unit
    /// tests use: APFS refuses such a name outright (<c>EILSEQ</c>, measured between 31 and 60 accents),
    /// whatever its length, so it cannot reach the second export on macOS.
    /// </remarks>
    [Fact]
    public async Task Exporting_a_name_of_one_huge_character_again_falls_back_to_a_plain_stem()
    {
        var oneElement = string.Concat(Enumerable.Repeat("\U0001F600‍", 35)) + "\U0001F600";
        Assert.Equal(1, new System.Globalization.StringInfo(oneElement).LengthInTextElements);
        Assert.Equal(249, Encoding.UTF8.GetByteCount(oneElement));
        var session = SeedSession(oneElement, "AI0", 1.25);
        var directory = Path.Combine(_root, "picked");
        Directory.CreateDirectory(directory);
        var folder = new LocalDirectoryFolder(directory);
        var writer = Writer();

        var first = await writer.WriteNewAsync(folder, new ExportFileNamer().NextCsvName(session.Name, session.ID), ExportTo(session));
        var second = await writer.WriteNewAsync(folder, new ExportFileNamer().NextCsvName(session.Name, session.ID), ExportTo(session));

        Assert.Equal(session.Name + ".csv", first);
        Assert.Equal("Session (2).csv", second);
        Assert.Empty(_logger.Errors);
    }

    /// <summary>A folder (not a file) with the export's name is taken too.</summary>
    [Fact]
    public async Task Export_all_steps_around_a_subfolder_with_the_same_name()
    {
        var session = SeedSession("Run", "AI0", 1.25);
        var folder = new MemoryStorageFolder("Documents");
        folder.SeedSubfolder("Run.csv");

        Assert.Equal("Run (2).csv", await Writer().WriteNewAsync(folder, "Run.csv", ExportTo(session)));
    }

    /// <summary>Export all: a session that fails creates nothing in the folder.</summary>
    [Fact]
    public async Task Export_all_creates_no_file_for_a_session_that_fails()
    {
        var folder = new MemoryStorageFolder("Documents");

        var name = await Writer().WriteNewAsync(folder, "Run.csv", _ => throw new IOException("database gone"));

        Assert.Null(name);
        Assert.Empty(folder.Files);
        AssertNoStagingLeft();
    }

    /// <summary>Export all: a folder that refuses to create the file is a failure, not a crash.</summary>
    [Fact]
    public async Task Export_all_reports_a_folder_that_refuses_to_create_the_file()
    {
        var session = SeedSession("Run", "AI0", 1.25);
        var folder = new MemoryStorageFolder("Documents") { RefuseCreate = true };

        var name = await Writer().WriteNewAsync(folder, "Run.csv", ExportTo(session));

        Assert.Null(name);
        Assert.Contains(_logger.Errors, e => e.Contains("Run.csv", StringComparison.Ordinal));
        AssertNoStagingLeft();
    }

    // ---- Name logic ----

    /// <summary>The second half of #587: the picker appends the extension, so the suggestion must
    /// not carry one. The Android picker builds its title as name + "." + DefaultExtension.</summary>
    [Theory]
    [InlineData("SD Import - log_20261104_104640", "SD Import - log_20261104_104640.csv")]
    [InlineData("a/b", "a_b.csv")]
    [InlineData("  ", "Session_7.csv")]
    public void The_suggested_name_gets_exactly_one_csv_extension_from_the_picker(string sessionName, string expected)
    {
        var suggested = LoggedSessionsMobileViewModel.SuggestedSaveName(sessionName, 7);

        Assert.False(suggested.EndsWith(".csv", StringComparison.OrdinalIgnoreCase), suggested);
        Assert.Equal(expected, suggested + "." + ExportFileNamer.CsvExtension);
    }

    [Fact]
    public void Export_all_names_have_one_extension_and_never_collide()
    {
        var namer = new ExportFileNamer();

        Assert.Equal("Run.csv", namer.NextCsvName("Run", 1));
        Assert.Equal("run (2).csv", namer.NextCsvName("run", 2));
        Assert.Equal("Session_3.csv", namer.NextCsvName(null, 3));
    }

    [Fact]
    public void The_export_all_status_names_the_folder_and_the_file()
    {
        Assert.Equal("Exported 1 session to Documents: Run.csv.",
            LoggedSessionsMobileViewModel.ExportAllStatus(["Run.csv"], 1, "Documents"));
        Assert.Equal("Exported 3 sessions to Documents: 3 files.",
            LoggedSessionsMobileViewModel.ExportAllStatus(["a.csv", "b.csv", "c.csv"], 3, "Documents"));
        Assert.StartsWith("Exported 1 of 2 sessions to Documents: Run.csv.",
            LoggedSessionsMobileViewModel.ExportAllStatus(["Run.csv"], 2, "Documents"));
        Assert.StartsWith("Couldn't export to Documents.",
            LoggedSessionsMobileViewModel.ExportAllStatus([], 2, "Documents"));
    }

    // ---- Helpers ----

    private StorageExportWriter Writer() => new(_logger, _staging);

    private Func<string, bool> ExportTo(LoggingSession session)
    {
        var exporter = new OptimizedLoggingSessionExporter(_contexts);
        return path => exporter.TryExportLoggingSession(
            session, path, exportRelativeTime: false, new Progress<int>(), CancellationToken.None, 0, 1);
    }

    /// <summary>What the desktop's path export writes for the same session: the stream export must
    /// be byte-for-byte the same CSV.</summary>
    private byte[] ExportByPath(LoggingSession session)
    {
        var path = Path.Combine(_root, "by-path-" + Guid.NewGuid().ToString("N") + ".csv");
        Assert.True(ExportTo(session)(path));
        return File.ReadAllBytes(path);
    }

    private void AssertNoStagingLeft() =>
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));

    private LoggingSession SeedSession(string name, string channelName, double value)
    {
        var id = ++_nextSessionId;
        using var context = _contexts.CreateDbContext();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var session = new LoggingSession { ID = id, Name = name, SessionStart = start };
        context.Sessions.Add(session);
        context.SaveChanges();

        for (var i = 0; i < 2; i++)
        {
            context.Samples.Add(new DataSample
            {
                LoggingSession = session,
                LoggingSessionID = session.ID,
                DeviceName = "Nyquist",
                DeviceSerialNo = "SERIAL-1",
                ChannelName = channelName,
                Color = "#FFD32F2F",
                Type = ChannelType.Analog,
                TimestampTicks = start.AddSeconds(i).Ticks,
                Value = value + i,
            });
        }

        context.SaveChanges();
        return new LoggingSession { ID = session.ID, Name = session.Name };
    }

    // ---- Create routing for Export all (review of PR #603) ----

    private const string ScopeNeedsEnforcedReadOnly =
        "Models an iOS folder outside the sandbox as a 0555 directory that only the fake scope makes writable, so it needs a host where 0555 binds on the current user.";

    /// <summary>
    /// A picked folder with no local path (every Android <c>content://</c> pick) is created through
    /// the platform's non-replacing create (review of PR #603), and the writer works with the file
    /// under the name the provider chose. The scope hook is for a folder WITH a path, so it is not
    /// asked.
    /// </summary>
    [Fact]
    public async Task A_folder_with_no_path_is_created_by_the_platforms_non_replacing_create()
    {
        var scopeAsked = false;
        var written = new MemoryStream();
        var deleted = false;
        var created = new ExclusiveFile(
            "Run (1).csv",
            () => Task.FromResult<Stream>(written),
            () => Task.FromResult<ulong?>(7),
            () => { deleted = true; return Task.CompletedTask; });

        var file = await StorageExportWriter.CreateNewFileRoutedAsync(
            directory: null,
            "Run.csv",
            () => throw new InvalidOperationException("a folder with no path has no local file to open"),
            () => { scopeAsked = true; return Task.FromResult<IScopedFolder?>(null); },
            () => Task.FromResult<ExclusiveFile?>(created));

        Assert.NotNull(file);
        Assert.False(scopeAsked, "The scope hook was asked about a folder with no path.");
        Assert.Equal("Run (1).csv", file.Name);
        Assert.Same(written, await file.OpenWriteAsync());
        Assert.Equal(7UL, await file.GetSizeAsync());
        await file.DeleteAsync();
        Assert.True(deleted, "Deleting the export's file did not reach the file the platform created.");
    }

    /// <summary>
    /// The finding (review of PR #603): where no non-replacing create applies, the create is
    /// REFUSED. It used to fall back to Avalonia's <c>CreateFileAsync</c>, which replaces a file of
    /// that name (<c>NSFileManager.CreateFile</c> on iOS). The routing no longer has that create to
    /// call at all, so this pins the refusal.
    /// </summary>
    [Fact]
    public async Task Without_a_non_replacing_create_the_export_is_refused_not_replaced()
    {
        var ex = await Assert.ThrowsAsync<IOException>(() => StorageExportWriter.CreateNewFileRoutedAsync(
            directory: null,
            "Run.csv",
            () => throw new InvalidOperationException("no local create was made"),
            () => Task.FromResult<IScopedFolder?>(null),
            () => Task.FromResult<ExclusiveFile?>(null)));

        Assert.Contains("cannot replace an existing file", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder with a usable local path (the desktop, an iOS folder inside the sandbox) is
    /// created in place with the create-if-absent, and neither hook is asked. A taken name is
    /// reported as taken (null), not replaced.
    /// </summary>
    [Fact]
    public async Task A_folder_with_a_usable_path_is_created_in_place_without_asking_either_hook()
    {
        var directory = Path.Combine(_root, "picked");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Run.csv"), PriorContent);
        var hookAsked = false;

        Task<StorageExportWriter.IExportFile?> Route(string name) => StorageExportWriter.CreateNewFileRoutedAsync(
            directory,
            name,
            () => Task.FromResult<StorageExportWriter.IExportFile?>(MemoryStorageFile.PickerCreated(name)),
            () => { hookAsked = true; return Task.FromResult<IScopedFolder?>(null); },
            () => { hookAsked = true; return Task.FromResult<ExclusiveFile?>(null); });

        Assert.Null(await Route("Run.csv"));
        var file = await Route("Run (2).csv");

        Assert.Equal("Run (2).csv", file?.Name);
        Assert.False(hookAsked, "A hook was asked about a folder whose path is usable.");
        Assert.Equal(PriorContent, File.ReadAllText(Path.Combine(directory, "Run.csv")));
        Assert.Equal(0, new FileInfo(Path.Combine(directory, "Run (2).csv")).Length);
    }

    /// <summary>
    /// The iOS case of the finding: the folder's path is refused outside its security scope, and
    /// no platform scope is available. The export is refused, and nothing in the folder changes.
    /// </summary>
    [EnforcedReadOnlyDirectoryFact(ScopeNeedsEnforcedReadOnly)]
    public async Task A_folder_refused_outside_its_scope_with_no_scope_hook_is_refused_not_replaced()
    {
        if (OperatingSystem.IsWindows()) { Assert.Fail(ScopeNeedsEnforcedReadOnly); return; }
        var directory = LockedDirectory("outside-sandbox", ("Run.csv", PriorContent));
        var platformAsked = false;

        await Assert.ThrowsAsync<IOException>(() => StorageExportWriter.CreateNewFileRoutedAsync(
            directory,
            "Run (2).csv",
            () => throw new InvalidOperationException("the path was refused, so nothing was created to open"),
            () => Task.FromResult<IScopedFolder?>(null),
            () => { platformAsked = true; return Task.FromResult<ExclusiveFile?>(null); }));

        Assert.True(platformAsked, "The platform's create was not tried before refusing.");
        Assert.Equal("Run.csv", Path.GetFileName(Assert.Single(Directory.GetFileSystemEntries(directory))));
        Assert.Equal(PriorContent, File.ReadAllText(Path.Combine(directory, "Run.csv")));
    }

    /// <summary>
    /// The fix for the iOS case (review of PR #603): a folder whose path is refused outside its
    /// security scope is created with the create-if-absent INSIDE the scope, and every later write,
    /// size check and delete runs inside it too. The write stream holds the scope until it is
    /// closed. The platform's create is not needed.
    /// </summary>
    [EnforcedReadOnlyDirectoryFact(ScopeNeedsEnforcedReadOnly)]
    public async Task A_folder_refused_outside_its_scope_is_created_and_written_inside_the_scope()
    {
        if (OperatingSystem.IsWindows()) { Assert.Fail(ScopeNeedsEnforcedReadOnly); return; }
        var directory = LockedDirectory("outside-sandbox");
        var scope = new ChmodScope(directory);
        var platformAsked = false;
        var path = Path.Combine(directory, "Run.csv");

        var file = await StorageExportWriter.CreateNewFileRoutedAsync(
            directory,
            "Run.csv",
            () => throw new InvalidOperationException("the path was refused, so nothing was created to open"),
            () => Task.FromResult<IScopedFolder?>(scope),
            () => { platformAsked = true; return Task.FromResult<ExclusiveFile?>(null); });

        Assert.NotNull(file);
        Assert.False(platformAsked, "The platform's create was asked although the scope served the folder.");
        Assert.Equal(0, scope.Depth);
        Assert.Equal(0, new FileInfo(path).Length);

        var bytes = Encoding.UTF8.GetBytes(PriorContent);
        await using (var stream = await file.OpenWriteAsync())
        {
            Assert.Equal(1, scope.Depth); // held while the stream is open
            await stream.WriteAsync(bytes);
        }

        Assert.Equal(0, scope.Depth);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal((ulong)bytes.Length, await file.GetSizeAsync());

        // A delete needs write access to the folder, so it succeeds only inside the scope.
        await file.DeleteAsync();
        Assert.False(File.Exists(path));
        Assert.Equal(0, scope.Depth);
        Assert.Throws<UnauthorizedAccessException>(() => File.Create(Path.Combine(directory, "outside.csv")));
    }

    /// <summary>Inside the scope the create still never replaces: a taken name is null, and the
    /// file keeps its bytes.</summary>
    [EnforcedReadOnlyDirectoryFact(ScopeNeedsEnforcedReadOnly)]
    public void The_create_inside_a_scope_never_replaces_an_existing_file()
    {
        if (OperatingSystem.IsWindows()) { Assert.Fail(ScopeNeedsEnforcedReadOnly); return; }
        var directory = LockedDirectory("outside-sandbox", ("Run.csv", PriorContent));
        var scope = new ChmodScope(directory);

        Assert.Null(StorageExportWriter.CreateNewInScope(scope, "Run.csv"));

        Assert.Equal(0, scope.Depth);
        Assert.Equal(PriorContent, File.ReadAllText(Path.Combine(directory, "Run.csv")));
    }

    /// <summary>
    /// The finding's race, end to end, in a folder reachable only inside its scope: a file appears
    /// under the checked name after the check. It keeps its bytes, and the export lands complete
    /// under the next free name.
    /// </summary>
    [EnforcedReadOnlyDirectoryFact(ScopeNeedsEnforcedReadOnly)]
    public async Task Export_all_into_a_scoped_folder_never_replaces_a_file_that_appears_after_the_name_check()
    {
        if (OperatingSystem.IsWindows()) { Assert.Fail(ScopeNeedsEnforcedReadOnly); return; }
        var session = SeedSession("Run", "AI0", 1.25);
        var directory = LockedDirectory("outside-sandbox");
        var scope = new ChmodScope(directory);
        var folder = new ScopedDirectoryFolder(scope) { AppearAfterCheck = ("Run.csv", PriorContent) };

        var name = await Writer().WriteNewAsync(folder, "Run.csv", ExportTo(session));

        Assert.Equal("Run (2).csv", name);
        Assert.Equal(PriorContent, File.ReadAllText(Path.Combine(directory, "Run.csv")));
        Assert.Equal(ExportByPath(session), File.ReadAllBytes(Path.Combine(directory, "Run (2).csv")));
        Assert.Equal(0, scope.Depth);
        AssertNoStagingLeft();
    }

    // ---- Avalonia bookmark decode (the iOS head's way to a scoped URL) ----

    /// <summary>The iOS head resolves a scoped URL from the folder's Avalonia bookmark. The decode
    /// must return exactly the native bytes after Avalonia's 16-byte header.</summary>
    [Fact]
    public void An_avalonia_ios_bookmark_yields_its_native_bytes()
    {
        byte[] native = [0x62, 0x6F, 0x6F, 0x6B, 0x00, 0xFF];

        var decoded = AvaloniaStorageBookmark.TryGetNativeBookmark(
            AvaloniaBookmark("ios", native), AvaloniaStorageBookmark.IosPlatformKey);

        Assert.Equal(native, decoded);
    }

    /// <summary>Anything that is not an Avalonia v1 bookmark for iOS yields nothing, so the head
    /// reports no scope rather than resolving the wrong bytes.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("YXZhLnYxLmlvcw==")] // "ava.v1.ios": a header with no bookmark after it
    public void A_string_that_is_not_an_ios_bookmark_yields_nothing(string? bookmark)
    {
        Assert.Null(AvaloniaStorageBookmark.TryGetNativeBookmark(bookmark, AvaloniaStorageBookmark.IosPlatformKey));
    }

    [Fact]
    public void A_bookmark_for_another_platform_yields_nothing()
    {
        byte[] native = [1, 2, 3];

        Assert.Null(AvaloniaStorageBookmark.TryGetNativeBookmark(AvaloniaBookmark("android", native), "ios"));
        // "iosx" shares the key's first bytes; the zero padding after the key must match too.
        Assert.Null(AvaloniaStorageBookmark.TryGetNativeBookmark(AvaloniaBookmark("iosx", native), "ios"));
        byte[] version2 = [.. "ava.v2.ios\0\0\0\0\0\0"u8, .. native];
        Assert.Null(AvaloniaStorageBookmark.TryGetNativeBookmark(Convert.ToBase64String(version2), "ios"));
    }

    /// <summary>Avalonia 12.1.6's encoding (<c>StorageBookmarkHelper.EncodeBookmark</c>): Base64 of
    /// "ava.v1." + platform key, zero-padded to 16 bytes, + the native bookmark.</summary>
    private static string AvaloniaBookmark(string platform, byte[] native)
    {
        var bytes = new byte[16 + native.Length];
        Encoding.ASCII.GetBytes("ava.v1." + platform).CopyTo(bytes, 0);
        native.CopyTo(bytes, 16);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>A 0555 directory, holding <paramref name="files"/>: a folder the app cannot write
    /// outside a scope. Made writable again in <see cref="Dispose"/> so it can be deleted.</summary>
    [UnsupportedOSPlatform("windows")]
    private string LockedDirectory(string name, params (string Name, string Content)[] files)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        foreach (var (fileName, content) in files)
        {
            File.WriteAllText(Path.Combine(directory, fileName), content);
        }

        File.SetUnixFileMode(directory, ChmodScope.Locked);
        _lockedDirectories.Add(directory);
        return directory;
    }

    /// <summary>
    /// Stands in for an iOS security scope: the directory is writable only while the scope is
    /// entered, as an out-of-sandbox folder is accessible only between
    /// <c>StartAccessingSecurityScopedResource</c> and <c>StopAccessingSecurityScopedResource</c>.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private sealed class ChmodScope(string directory) : IScopedFolder
    {
        internal const UnixFileMode Locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        private const UnixFileMode Open = Locked | UnixFileMode.UserWrite;

        /// <summary>How many entries are open now.</summary>
        internal int Depth { get; private set; }

        public string LocalPath => directory;

        public IDisposable Enter()
        {
            if (Depth++ == 0)
            {
                File.SetUnixFileMode(directory, Open);
            }

            return new Exit(this);
        }

        private sealed class Exit(ChmodScope owner) : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) { return; }
                _done = true;
                if (--owner.Depth == 0)
                {
                    File.SetUnixFileMode(owner.LocalPath, Locked);
                }
            }
        }
    }

    /// <summary>
    /// The writer's folder seam over a real directory that is reachable only inside its scope,
    /// creating through the production routing. <see cref="AppearAfterCheck"/> makes a file appear
    /// right after the first name check for it reports the name free, as cloud sync could.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private sealed class ScopedDirectoryFolder(ChmodScope scope) : StorageExportWriter.IExportFolder
    {
        internal (string Name, string Content)? AppearAfterCheck { get; set; }

        public string Name => Path.GetFileName(scope.LocalPath);

        public Task<bool> ContainsAsync(string name)
        {
            var path = Path.Combine(scope.LocalPath, name);
            var contains = File.Exists(path) || Directory.Exists(path);
            if (!contains && AppearAfterCheck is { } appear && appear.Name == name)
            {
                AppearAfterCheck = null;
                using (scope.Enter())
                {
                    File.WriteAllText(path, appear.Content);
                }
            }

            return Task.FromResult(contains);
        }

        public Task<StorageExportWriter.IExportFile?> CreateNewFileAsync(string name) =>
            StorageExportWriter.CreateNewFileRoutedAsync(
                scope.LocalPath,
                name,
                () => throw new InvalidOperationException("the path is refused outside the scope, so the local create cannot succeed"),
                () => Task.FromResult<IScopedFolder?>(scope),
                () => Task.FromResult<ExclusiveFile?>(null));
    }

    /// <summary>
    /// The writer's folder seam over a plain writable directory, as a desktop or in-sandbox iOS pick
    /// is: it creates through the production routing, so the real create-if-absent
    /// (<see cref="StorageExportWriter.TryCreateNewLocalFile"/>) meets the real file system.
    /// </summary>
    private sealed class LocalDirectoryFolder(string directory) : StorageExportWriter.IExportFolder
    {
        public string Name => Path.GetFileName(directory);

        public Task<bool> ContainsAsync(string name)
        {
            var path = Path.Combine(directory, name);
            return Task.FromResult(File.Exists(path) || Directory.Exists(path));
        }

        public Task<StorageExportWriter.IExportFile?> CreateNewFileAsync(string name) =>
            StorageExportWriter.CreateNewFileRoutedAsync(
                directory,
                name,
                () => Task.FromResult<StorageExportWriter.IExportFile?>(new LocalFile(Path.Combine(directory, name))),
                () => Task.FromResult<IScopedFolder?>(null),
                () => Task.FromResult<ExclusiveFile?>(null));

        /// <summary>A file the local create made, opened by path.</summary>
        private sealed class LocalFile(string path) : StorageExportWriter.IExportFile
        {
            public string Name => Path.GetFileName(path);

            public Task<Stream> OpenWriteAsync() =>
                Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None));

            public Task<ulong?> GetSizeAsync() => Task.FromResult<ulong?>((ulong)new FileInfo(path).Length);

            public Task DeleteAsync()
            {
                File.Delete(path);
                return Task.CompletedTask;
            }
        }
    }

    /// <summary>An in-memory file like an Android document: no local path, reachable only through
    /// its streams.</summary>
    private sealed class MemoryStorageFile : StorageExportWriter.IExportFile
    {
        private byte[] _content;

        private MemoryStorageFile(string name, byte[] content)
        {
            Name = name;
            _content = content;
        }

        /// <summary>What the Android and iOS save pickers return: a file that already exists, empty.</summary>
        internal static MemoryStorageFile PickerCreated(string name) => new(name, []);

        internal static MemoryStorageFile Existing(string name, string content) => new(name, Encoding.UTF8.GetBytes(content));

        public string Name { get; }

        internal bool Deleted { get; private set; }
        internal bool FailOpenWrite { get; set; }
        internal bool TruncateOnOpen { get; set; } = true;
        internal int? FailAfterBytes { get; set; }
        internal int OpenWriteCount { get; private set; }

        internal byte[] Bytes => _content;

        /// <summary>The content as text, without the byte-order mark the exporter writes.</summary>
        internal string Text => Encoding.UTF8.GetString(_content).TrimStart('\uFEFF');

        public Task<ulong?> GetSizeAsync() => Task.FromResult<ulong?>((ulong)_content.Length);

        public Task DeleteAsync()
        {
            Deleted = true;
            _content = [];
            return Task.CompletedTask;
        }

        public Task<Stream> OpenWriteAsync()
        {
            OpenWriteCount++;
            if (FailOpenWrite) { throw new UnauthorizedAccessException("read-only location"); }
            if (TruncateOnOpen) { _content = []; }
            return Task.FromResult<Stream>(new WriteBackStream(this, _content, FailAfterBytes));
        }

        /// <summary>Commits each write back to the file, as a real document does, so a failure
        /// part-way leaves a partial file behind for the writer to deal with.</summary>
        private sealed class WriteBackStream : MemoryStream
        {
            private readonly MemoryStorageFile _owner;
            private readonly int? _failAfter;

            public WriteBackStream(MemoryStorageFile owner, byte[] initial, int? failAfter)
            {
                _owner = owner;
                _failAfter = failAfter;
                base.Write(initial, 0, initial.Length);
                Position = 0;
            }

            // Every write funnels into base.Write(byte[], int, int): MemoryStream's span overload
            // calls back into an overridden array overload, so overriding both recursed.
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_failAfter is { } limit && Position + count > limit)
                {
                    base.Write(buffer, offset, (int)Math.Max(0, limit - Position));
                    _owner._content = ToArray();
                    throw new IOException("device storage full");
                }

                base.Write(buffer, offset, count);
                _owner._content = ToArray();
            }

            public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Write(buffer.Span);
                return ValueTask.CompletedTask;
            }

            public override void SetLength(long value)
            {
                base.SetLength(value);
                _owner._content = ToArray();
            }
        }
    }

    /// <summary>An in-memory folder like an Android document tree. CreateNewFileAsync keeps the
    /// writer's contract: an atomic create-if-absent that returns null for a taken name.</summary>
    private sealed class MemoryStorageFolder(string name) : StorageExportWriter.IExportFolder
    {
        private readonly HashSet<string> _subfolders = new();

        internal Dictionary<string, MemoryStorageFile> Files { get; } = new();

        internal bool RefuseCreate { get; init; }

        /// <summary>Makes every file this folder creates — and a file that appears before a create —
        /// fail its copy after this many bytes.</summary>
        internal int? FailNewFilesAfterBytes { get; init; }

        public string Name { get; } = name;

        internal void Seed(string fileName, string content) => Files[fileName] = MemoryStorageFile.Existing(fileName, content);

        internal void SeedSubfolder(string folderName) => _subfolders.Add(folderName);

        private (string Name, string Content)? _appearBeforeCreate;

        /// <summary>Puts a file called <paramref name="fileName"/> into the folder at the start of the
        /// next create — after the writer's name check has seen the name free.</summary>
        internal void AppearBeforeCreate(string fileName, string content) => _appearBeforeCreate = (fileName, content);

        public Task<bool> ContainsAsync(string itemName) =>
            Task.FromResult(Files.ContainsKey(itemName) || _subfolders.Contains(itemName));

        public Task<StorageExportWriter.IExportFile?> CreateNewFileAsync(string fileName)
        {
            if (_appearBeforeCreate is { } appear)
            {
                _appearBeforeCreate = null;
                Seed(appear.Name, appear.Content);
                // Storage is full for it too, so a writer that copies into it shows its cleanup.
                Files[appear.Name].FailAfterBytes = FailNewFilesAfterBytes;
            }

            if (RefuseCreate) { throw new IOException("the folder refused to create the file"); }
            if (Files.ContainsKey(fileName) || _subfolders.Contains(fileName))
            {
                return Task.FromResult<StorageExportWriter.IExportFile?>(null);
            }

            var file = MemoryStorageFile.PickerCreated(fileName);
            file.FailAfterBytes = FailNewFilesAfterBytes;
            Files[fileName] = file;
            return Task.FromResult<StorageExportWriter.IExportFile?>(file);
        }
    }
}
