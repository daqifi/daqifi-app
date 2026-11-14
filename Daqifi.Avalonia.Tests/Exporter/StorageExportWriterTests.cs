using System.Text;
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

    /// <summary>Export all: each session becomes its own file in a content-URI folder, and the name
    /// returned is the one the provider chose.</summary>
    [Fact]
    public async Task Export_all_creates_one_complete_file_per_session_in_a_content_uri_folder()
    {
        var first = SeedSession("Run", "AI0", 1.25);
        var second = SeedSession("Run", "AI1", 7.5);
        var folder = new MemoryStorageFolder("Documents") { RenameDuplicates = true };
        folder.Seed("Run.csv", PriorContent); // the provider will call the new one "Run (1).csv"
        var namer = new ExportFileNamer();
        var writer = Writer();

        var a = await writer.WriteNewAsync(folder, namer.NextCsvName(first.Name, first.ID), ExportTo(first));
        var b = await writer.WriteNewAsync(folder, namer.NextCsvName(second.Name, second.ID), ExportTo(second));

        Assert.Equal("Run (1).csv", a);
        Assert.Equal("Run (2).csv", b);
        Assert.Equal(ExportByPath(first), folder.Files["Run (1).csv"].Bytes);
        Assert.Equal(ExportByPath(second), folder.Files["Run (2).csv"].Bytes);
        Assert.Equal(PriorContent, folder.Files["Run.csv"].Text);
        AssertNoStagingLeft();
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

    /// <summary>An in-memory folder like an Android document tree.</summary>
    private sealed class MemoryStorageFolder(string name) : StorageExportWriter.IExportFolder
    {
        internal Dictionary<string, MemoryStorageFile> Files { get; } = new();

        /// <summary>A provider that picks "name (1).csv" for a name that is taken, instead of
        /// replacing the file.</summary>
        internal bool RenameDuplicates { get; init; }

        internal bool RefuseCreate { get; init; }

        public string Name { get; } = name;

        internal void Seed(string fileName, string content) => Files[fileName] = MemoryStorageFile.Existing(fileName, content);

        public Task<StorageExportWriter.IExportFile?> CreateFileAsync(string fileName)
        {
            if (RefuseCreate) { return Task.FromResult<StorageExportWriter.IExportFile?>(null); }

            var actual = fileName;
            if (RenameDuplicates)
            {
                var stem = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                for (var n = 1; Files.ContainsKey(actual); n++) { actual = $"{stem} ({n}){ext}"; }
            }

            var file = MemoryStorageFile.PickerCreated(actual);
            Files[actual] = file;
            return Task.FromResult<StorageExportWriter.IExportFile?>(file);
        }
    }
}
