using System.Text;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Sentry;
using Sentry.Extensibility;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins what the export dialog does and says when a session cannot be written (issue #683).
///
/// <para>
/// The regression: for any failure other than a denied folder, a missing folder or a locked file,
/// the dialog said only "Export failed. Please try again." — wrong advice for a full disk, and for a
/// session name too long for a file name, since a retry fails the same way. Export All left its loop
/// at the first failure, so the files before it were in the folder and the files after it were not,
/// and nothing said which. A long name also went to Sentry as two Error events for each attempt.
/// </para>
///
/// <para>
/// End to end through the dialog's own export command, the real exporter and a real SQLite database,
/// so "the file is there" means a file on disk. A full disk cannot be made portably on a CI runner,
/// so it is injected: the context factory throws the <see cref="IOException"/> the runtime raises for
/// <c>ENOSPC</c> while the session's CSV is being written. The real thing was measured once on macOS
/// against a 3 MB disk image; see the PR.
/// </para>
/// </summary>
public sealed class ExportFailureReportingTests : IDisposable
{
    private readonly ExportFixture _fixture = new("daqifi-export-failures-");

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// The reported case A: five sessions, two of them with names too long for a file name — 300
    /// ASCII characters, and 90 Chinese characters (270 bytes of UTF-8). Every one is now written,
    /// under a name the file system takes.
    /// </summary>
    [Fact]
    public async Task Export_all_writes_every_session_when_some_names_are_too_long_for_a_file_name()
    {
        var sessions = new[]
        {
            _fixture.Seed("Run A", "AI0", 1.25),
            _fixture.Seed(new string('x', 300), "AI1", 2.5),
            _fixture.Seed("Run C", "AI2", 3.75),
            _fixture.Seed(string.Concat(Enumerable.Repeat("数据", 45)), "AI3", 5.0),
            _fixture.Seed("Run E", "AI4", 6.25),
        };

        var viewModel = new ExportDialogViewModel(_fixture.Contexts, sessions);
        await viewModel.ExportToDirectoryAsync(_fixture.Destination);

        Assert.True(viewModel.ExportSucceeded, viewModel.ExportResultMessage);
        var files = Directory.GetFiles(_fixture.Destination).Select(Path.GetFileName).ToList();
        Assert.Equal(5, files.Count);
        Assert.All(files, name => Assert.True(Encoding.UTF8.GetByteCount(name!) <= 255, name));
        Assert.Contains("Run A.csv", files);
        Assert.Contains("Run E.csv", files);
        Assert.Contains(new string('x', 251) + ".csv", files);
        Assert.Contains(string.Concat(Enumerable.Repeat("数据", 41)) + "数.csv", files);
    }

    /// <summary>
    /// Two long names that differ only after the cut get a file each, each holding its own
    /// session's rows — the cut must not bring back the truncating collision of issue #186.
    /// </summary>
    [Fact]
    public async Task Two_names_that_are_the_same_after_the_cut_each_get_their_own_file()
    {
        var first = _fixture.Seed(new string('y', 299) + "1", "AI0", 1.25);
        var second = _fixture.Seed(new string('y', 299) + "2", "AI1", 2.5);

        var viewModel = new ExportDialogViewModel(_fixture.Contexts, new[] { first, second });
        await viewModel.ExportToDirectoryAsync(_fixture.Destination);

        Assert.True(viewModel.ExportSucceeded, viewModel.ExportResultMessage);
        var firstCsv = await File.ReadAllTextAsync(Path.Combine(_fixture.Destination, new string('y', 251) + ".csv"));
        var secondCsv = await File.ReadAllTextAsync(Path.Combine(_fixture.Destination, new string('y', 247) + " (2).csv"));
        Assert.Contains("AI0", firstCsv, StringComparison.Ordinal);
        Assert.DoesNotContain("AI1", firstCsv, StringComparison.Ordinal);
        Assert.Contains("AI1", secondCsv, StringComparison.Ordinal);
        Assert.DoesNotContain("AI0", secondCsv, StringComparison.Ordinal);
    }

    /// <summary>
    /// One session that cannot be written no longer keeps the sessions after it out of the folder.
    /// The failure here is real and portable: a folder already sits at the second session's file
    /// name, so its finished CSV cannot be moved into place. The pre-flight does not see it, because
    /// it probes only a FILE that is already there.
    /// </summary>
    [Fact]
    public async Task Export_all_goes_on_past_a_session_that_cannot_be_written_and_says_which_one()
    {
        var sessions = new[]
        {
            _fixture.Seed("Run A", "AI0", 1.25),
            _fixture.Seed("Run B", "AI1", 2.5),
            _fixture.Seed("Run C", "AI2", 3.75),
        };
        Directory.CreateDirectory(Path.Combine(_fixture.Destination, "Run B.csv"));

        var viewModel = new ExportDialogViewModel(_fixture.Contexts, sessions);
        await viewModel.ExportToDirectoryAsync(_fixture.Destination);

        Assert.True(viewModel.IsExportComplete);
        Assert.False(viewModel.ExportSucceeded);
        Assert.True(File.Exists(Path.Combine(_fixture.Destination, "Run A.csv")), _fixture.Present());
        Assert.True(File.Exists(Path.Combine(_fixture.Destination, "Run C.csv")), _fixture.Present());
        var message = viewModel.ExportResultMessage ?? string.Empty;
        Assert.StartsWith("Exported 2 of 3 sessions. ", message, StringComparison.Ordinal);
        Assert.Contains("'Run B.csv'", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Please try again", message, StringComparison.Ordinal);
        _fixture.AssertNoStagingLeftovers();
    }

    /// <summary>
    /// The reported case B: a disk that fills while the second of three sessions is written. The
    /// message names that file, says the disk is full, and says how many files the folder holds; the
    /// third session is still written, and the staging file of the failed one is gone.
    /// </summary>
    [Fact]
    public async Task A_full_disk_is_named_with_the_file_and_the_count_and_the_rest_are_still_written()
    {
        var sessions = new[]
        {
            _fixture.Seed("Small A", "AI0", 1.25),
            _fixture.Seed("Big B", "AI1", 2.5),
            _fixture.Seed("Small C", "AI2", 3.75),
        };
        var firstFile = Path.Combine(_fixture.Destination, "Small A.csv");
        var contexts = _fixture.FailWhileWriting(() => File.Exists(firstFile), DiskFull);

        var viewModel = new ExportDialogViewModel(contexts, sessions);
        await viewModel.ExportToDirectoryAsync(_fixture.Destination);

        Assert.False(viewModel.ExportSucceeded);
        Assert.Equal(
            "Exported 2 of 3 sessions. Could not write 'Big B.csv' — the disk is full. " +
            "Make space on the disk, or choose a different location.",
            viewModel.ExportResultMessage);
        Assert.True(File.Exists(firstFile), _fixture.Present());
        Assert.False(File.Exists(Path.Combine(_fixture.Destination, "Big B.csv")), _fixture.Present());
        Assert.True(File.Exists(Path.Combine(_fixture.Destination, "Small C.csv")), _fixture.Present());
        _fixture.AssertNoStagingLeftovers();
    }

    /// <summary>
    /// When more than one session is not exported, the sentence after the first failure counts the
    /// others, so the count in the lead adds up.
    /// </summary>
    [Fact]
    public async Task Further_sessions_that_are_not_exported_are_counted()
    {
        var sessions = new[]
        {
            _fixture.Seed("Small A", "AI0", 1.25),
            _fixture.Seed("Big B", "AI1", 2.5),
            _fixture.Seed("Big C", "AI2", 3.75),
        };
        var firstFile = Path.Combine(_fixture.Destination, "Small A.csv");
        var contexts = _fixture.FailWhileWriting(() => File.Exists(firstFile), DiskFull, once: false);

        var viewModel = new ExportDialogViewModel(contexts, sessions);
        await viewModel.ExportToDirectoryAsync(_fixture.Destination);

        Assert.Equal(
            "Exported 1 of 3 sessions. Could not write 'Big B.csv' — the disk is full. " +
            "Make space on the disk, or choose a different location. 1 other session was not exported either.",
            viewModel.ExportResultMessage);
    }

    /// <summary>
    /// When nothing at all was written, the lead says so instead of "Exported 0 of 2".
    /// </summary>
    [Fact]
    public async Task When_every_session_fails_the_message_says_nothing_was_exported()
    {
        var sessions = new[]
        {
            _fixture.Seed("Big A", "AI0", 1.25),
            _fixture.Seed("Big B", "AI1", 2.5),
            _fixture.Seed("Big C", "AI2", 3.75),
        };
        var contexts = _fixture.FailWhileWriting(() => true, DiskFull, once: false);

        var viewModel = new ExportDialogViewModel(contexts, sessions);
        await viewModel.ExportToDirectoryAsync(_fixture.Destination);

        Assert.Equal(
            "Nothing was exported. Could not write 'Big A.csv' — the disk is full. " +
            "Make space on the disk, or choose a different location. 2 other sessions were not exported either.",
            viewModel.ExportResultMessage);
        Assert.Empty(Directory.GetFiles(_fixture.Destination));
    }

    /// <summary>
    /// A one-session export keeps the plain message: there is nothing to count.
    /// </summary>
    [Fact]
    public async Task A_single_export_to_a_full_disk_says_the_disk_is_full()
    {
        var session = _fixture.Seed("Big B", "AI1", 2.5);
        var destination = Path.Combine(_fixture.Destination, "readings.csv");
        var viewModel = new ExportDialogViewModel(_fixture.FailWhileWriting(() => true, DiskFull), session.ID)
        {
            ExportFilePath = destination,
        };

        await viewModel.ExportLoggingSessionsCommand.ExecuteAsync(null);

        Assert.False(viewModel.ExportSucceeded);
        Assert.Equal(
            "Could not write 'readings.csv' — the disk is full. Make space on the disk, or choose a different location.",
            viewModel.ExportResultMessage);
        Assert.False(File.Exists(destination), _fixture.Present());
        _fixture.AssertNoStagingLeftovers();
    }

    /// <summary>
    /// A failure the classifier does not know is named by file, and NOT with the system's own
    /// message, which names the staging file (<c>daqifi-exporting-…</c>) the user never sees.
    /// </summary>
    [Fact]
    public async Task An_unknown_failure_names_the_file_and_not_the_staging_file()
    {
        var session = _fixture.Seed("Run B", "AI1", 2.5);
        var destination = Path.Combine(_fixture.Destination, "readings.csv");
        var viewModel = new ExportDialogViewModel(
            _fixture.FailWhileWriting(() => true, staging => new IOException($"Input/output error : '{staging}'")),
            session.ID)
        {
            ExportFilePath = destination,
        };

        await viewModel.ExportLoggingSessionsCommand.ExecuteAsync(null);

        Assert.False(viewModel.ExportSucceeded);
        Assert.Equal(
            "Could not export 'readings.csv' because of an unexpected error. The log has the details.",
            viewModel.ExportResultMessage);
    }

    /// <summary>
    /// A file name the file system refuses, typed straight into the save picker, is the user's to
    /// fix by choosing again, and the message says so instead of "Please try again".
    /// </summary>
    [Fact]
    public async Task A_single_export_to_a_name_too_long_for_the_file_system_says_so()
    {
        var session = _fixture.Seed("Run B", "AI1", 2.5);
        var destination = Path.Combine(_fixture.Destination, new string('z', 300) + ".csv");
        var viewModel = new ExportDialogViewModel(_fixture.Contexts, session.ID) { ExportFilePath = destination };

        await viewModel.ExportLoggingSessionsCommand.ExecuteAsync(null);

        Assert.False(viewModel.ExportSucceeded);
        Assert.Contains("is too long", viewModel.ExportResultMessage ?? string.Empty, StringComparison.Ordinal);
        _fixture.AssertNoStagingLeftovers();
    }

    /// <summary>
    /// A cancel is still a cancel, and not one more failure for the loop to go on past: the dialog
    /// goes back to its form with nothing to report, and the session after the cancelled one is not
    /// started. Cancelled while the LAST session writes, which is the case where treating the cancel
    /// as a failure would otherwise reach the result screen.
    /// </summary>
    [Fact]
    public async Task Cancelling_during_a_write_still_cancels_the_export()
    {
        var session = _fixture.Seed("Run A", "AI0", 1.25);
        var destination = Path.Combine(_fixture.Destination, "readings.csv");
        ExportDialogViewModel? viewModel = null;
        var contexts = _fixture.WhileWriting(() => true, () => viewModel!.CancelExportCommand.Execute(null));
        viewModel = new ExportDialogViewModel(contexts, session.ID) { ExportFilePath = destination };

        await viewModel.ExportLoggingSessionsCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsExportComplete, viewModel.ExportResultMessage);
        Assert.True(viewModel.IsConfiguring);
        Assert.Null(viewModel.ExportResultMessage);
        Assert.False(File.Exists(destination), _fixture.Present());
    }

    /// <summary>
    /// The <see cref="IOException"/> the runtime raises when a write meets a full disk: on macOS and
    /// Linux its HResult is the raw errno (<c>ENOSPC</c>, 28 on both), on Windows the FACILITY_WIN32
    /// form of <c>ERROR_DISK_FULL</c>. The message carries the staging file's path, as the real one
    /// does, so a message that passes it through is caught.
    /// </summary>
    internal static IOException DiskFull(string stagingPath) =>
        new($"No space left on device : '{stagingPath}'",
            OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);
}

/// <summary>
/// The Sentry half of issue #683: how many Error events one failed export sends. The exporter
/// logged each failure at Error and threw it, and the dialog logged it at Error again, so every
/// attempt was two events — and a name too long for the file system, which is the user's data and
/// not an app fault, was two events too.
/// </summary>
/// <remarks>
/// Read off the process-wide Sentry hub, which is what <c>AppLogger.Error</c> reports to, hence the
/// non-parallel collection. Breadcrumbs are read as well, as the positive control: a count of zero
/// events means nothing unless the failure was logged somewhere.
/// </remarks>
[Collection(SentryHubCollection.Name)]
public sealed class ExportFailureSentryTests : IDisposable
{
    private readonly ExportFixture _fixture = new("daqifi-export-sentry-");
    private readonly List<SentryEvent> _events = [];
    private readonly List<string> _breadcrumbs = [];
    private readonly IDisposable _sentry;

    public ExportFailureSentryTests()
    {
        _sentry = SentrySdk.Init(options =>
        {
            options.Dsn = "https://0123456789abcdef0123456789abcdef@o0.ingest.us.sentry.invalid/1";
            options.BackgroundWorker = new DiscardingWorker();
            options.AutoSessionTracking = false;
            options.DisableAppDomainUnhandledExceptionCapture();
            options.DisableUnobservedTaskExceptionCapture();
            options.SetBeforeSend((sentryEvent, _) =>
            {
                lock (_events) { _events.Add(sentryEvent); }
                return null;
            });
            options.SetBeforeBreadcrumb(breadcrumb =>
            {
                lock (_breadcrumbs) { _breadcrumbs.Add(breadcrumb.Message ?? string.Empty); }
                return null;
            });
        });
    }

    public void Dispose()
    {
        _sentry.Dispose();
        _fixture.Dispose();
    }

    /// <summary>A name the file system refuses is a Warning: no Sentry event at all.</summary>
    [Fact]
    public async Task A_name_too_long_for_the_file_system_sends_no_error_event()
    {
        var session = _fixture.Seed("Run B", "AI1", 2.5);
        var destination = Path.Combine(_fixture.Destination, new string('z', 300) + ".csv");
        var viewModel = new ExportDialogViewModel(_fixture.Contexts, session.ID) { ExportFilePath = destination };

        await viewModel.ExportLoggingSessionsCommand.ExecuteAsync(null);

        Assert.False(viewModel.ExportSucceeded);
        Assert.Empty(Events());
        Assert.Contains(Breadcrumbs(), message => message.Contains("could not write", StringComparison.Ordinal)
                                                  && message.Contains(nameof(PathTooLongException), StringComparison.Ordinal));
    }

    /// <summary>
    /// A full disk keeps its Error treatment — the issue did not ask to change that — but it is one
    /// event for one failure, not two.
    /// </summary>
    [Fact]
    public async Task A_full_disk_sends_one_error_event_not_two()
    {
        var session = _fixture.Seed("Big B", "AI1", 2.5);
        var viewModel = new ExportDialogViewModel(
            _fixture.FailWhileWriting(() => true, ExportFailureReportingTests.DiskFull), session.ID)
        {
            ExportFilePath = Path.Combine(_fixture.Destination, "readings.csv"),
        };

        await viewModel.ExportLoggingSessionsCommand.ExecuteAsync(null);

        Assert.False(viewModel.ExportSucceeded);
        var sent = Assert.Single(Events());
        Assert.IsType<IOException>(sent.Exception);
    }

    private List<SentryEvent> Events()
    {
        lock (_events) { return [.. _events]; }
    }

    private List<string> Breadcrumbs()
    {
        lock (_breadcrumbs) { return [.. _breadcrumbs]; }
    }

    /// <summary>Accepts envelopes and sends none, so the SDK never builds an HTTP transport.</summary>
    private sealed class DiscardingWorker : IBackgroundWorker
    {
        public int QueuedItems => 0;

        public bool EnqueueEnvelope(Sentry.Protocol.Envelopes.Envelope envelope) => true;

        public Task FlushAsync(TimeSpan timeout) => Task.CompletedTask;
    }
}

/// <summary>
/// A throwaway database and export folder, and the context factories that inject a failure or a
/// cancel while a session's CSV is being written. Never the real DAQiFi data directory.
/// </summary>
internal sealed class ExportFixture : IDisposable
{
    private readonly string _root;
    private int _nextSessionId;

    public ExportFixture(string prefix)
    {
        _root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Destination = Path.Combine(_root, "export");
        Directory.CreateDirectory(Destination);
        Contexts = TestDatabase.Contexts(Path.Combine(_root, "DAQiFiDatabase.db"));
        using var context = Contexts.CreateDbContext();
        context.Database.EnsureCreated();
    }

    public IDbContextFactory<LoggingContext> Contexts { get; }

    /// <summary>The export folder. Holds nothing but what the export writes.</summary>
    public string Destination { get; }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort — a leftover temp directory must not fail a test */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>Writes one session with one sample and returns the detached row (id + name) the
    /// Logged Data list would hand the dialog.</summary>
    public LoggingSession Seed(string name, string channelName, double value)
    {
        // Session ids are assigned by the app, not by SQLite, so the seeder supplies its own.
        var id = ++_nextSessionId;
        using var context = Contexts.CreateDbContext();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var session = new LoggingSession { ID = id, Name = name, SessionStart = start };
        context.Sessions.Add(session);
        context.SaveChanges();

        context.Samples.Add(new DataSample
        {
            LoggingSession = session,
            LoggingSessionID = session.ID,
            DeviceName = "Nyquist",
            DeviceSerialNo = "SERIAL-1",
            ChannelName = channelName,
            Color = "#FFD32F2F",
            Type = ChannelType.Analog,
            TimestampTicks = start.Ticks,
            Value = value,
        });
        context.SaveChanges();

        return new LoggingSession { ID = session.ID, Name = name };
    }

    /// <summary>
    /// A context factory that throws <paramref name="failure"/> from the first context opened while
    /// a CSV is being written — that is, while a staging file is in the export folder — and
    /// <paramref name="when"/> is true. Keyed on the staging file, not on a count of contexts, so it
    /// does not depend on how many queries one session's export makes. With <paramref name="once"/>
    /// false it throws for every session that matches.
    /// </summary>
    public IDbContextFactory<LoggingContext> FailWhileWriting(
        Func<bool> when, Func<string, Exception> failure, bool once = true) =>
        new HookedContexts(Contexts, () => Staging() is { } staging && when() ? staging : null,
            staging => throw failure(staging), once);

    /// <summary>As <see cref="FailWhileWriting"/>, but runs <paramref name="action"/> instead.</summary>
    public IDbContextFactory<LoggingContext> WhileWriting(Func<bool> when, Action action) =>
        new HookedContexts(Contexts, () => Staging() is { } staging && when() ? staging : null,
            _ => action(), once: true);

    /// <summary>No staging file was left in the export folder.</summary>
    public void AssertNoStagingLeftovers() =>
        Assert.True(Staging() == null, $"the export left a staging file behind: {Present()}");

    /// <summary>What is in the export folder, for an assertion message that says why.</summary>
    public string Present() =>
        "[" + string.Join(", ", Directory.GetFileSystemEntries(Destination).Select(Path.GetFileName)) + "]";

    private string? Staging() =>
        Directory.EnumerateFiles(Destination, "daqifi-exporting-*").FirstOrDefault();

    /// <summary>Wraps the real factory and runs <c>hook</c> when <c>trigger</c> returns a staging path.</summary>
    private sealed class HookedContexts(
        IDbContextFactory<LoggingContext> inner, Func<string?> trigger, Action<string> hook, bool once)
        : IDbContextFactory<LoggingContext>
    {
        private int _fired;

        public LoggingContext CreateDbContext()
        {
            if ((!once || Volatile.Read(ref _fired) == 0) && trigger() is { } staging)
            {
                Interlocked.Exchange(ref _fired, 1);
                hook(staging);
            }

            return inner.CreateDbContext();
        }
    }
}
