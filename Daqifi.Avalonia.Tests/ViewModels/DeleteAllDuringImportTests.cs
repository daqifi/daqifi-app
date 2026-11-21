using System.Collections.ObjectModel;
using Daqifi.Desktop.DialogService;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// #682: DELETE ALL was accepted while an SD card IMPORT ALL ran. The purge moves the database file
/// aside and builds an empty one, so the file the import was writing failed with "disk I/O error",
/// the result dialog blamed the device connection, and the import then wrote the rest of its files
/// into the new database, so the list that DELETE ALL emptied filled again.
/// </summary>
/// <remarks>
/// <para>The purge and every import now claim one <see cref="SessionStoreGate"/>. These tests cover
/// the list view model's half, in both orders: an import holds the store, so DELETE ALL is refused
/// (before the confirmation, or after it when the import started while it was open), and DELETE
/// ALL holds the store, so an import cannot start while the purge runs (#682 review: the import of
/// earlier sessions, started from the bell just after the confirmation, was deleted with the rest).</para>
/// <para>The purge resolves the logging-context factory first. These tests resolve it through a
/// callback that counts, may try to start an import, and then throws, so nothing on disk is touched.</para>
/// </remarks>
public class DeleteAllDuringImportTests
{
    private const string WaitForImport = "Wait for the import to finish before you delete all sessions.";

    /// <summary>The issue's steps: an import runs, the user clicks DELETE ALL.</summary>
    [Fact]
    public async Task DeleteAll_WhileAnImportHoldsTheStore_IsRefusedAndTheSessionsStay()
    {
        var session = new LoggingSession(0, "SD Import - log_1");
        var host = new Host(session);
        using var import = host.SessionStore.TryBeginImport();
        var purges = 0;
        var listViewModel = NewListViewModel(host, () => purges++);

        await listViewModel.DeleteAllSessionsAsync();

        Assert.Equal(0, purges);
        Assert.Equal(0, host.Confirmations);
        Assert.Equal(("Cannot Delete", WaitForImport), Assert.Single(host.MessagesShown));
        Assert.Same(session, Assert.Single(host.LoggingSessions));
        Assert.Equal(0, host.Suspends);
    }

    /// <summary>
    /// The confirmation covers only the Logged Data pane. Logging, or the import of sessions from an
    /// earlier version, can start from outside it while it is open, so DELETE ALL looks again after
    /// the user answers: at logging, and by claiming the store.
    /// </summary>
    [Theory]
    [InlineData(false, "Wait for the import to finish before you delete all sessions.")]
    [InlineData(true, "Please stop logging before deleting all sessions.")]
    public async Task DeleteAll_WhenWorkStartsWhileTheConfirmationIsOpen_IsRefused(bool logging, string expected)
    {
        var session = new LoggingSession(0, "Session_0");
        var host = new Host(session);
        IDisposable? import = null;
        host.WhileConfirming = () =>
        {
            if (logging) { host.IsLoggingActive = true; } else { import = host.SessionStore.TryBeginImport(); }
        };
        var purges = 0;
        var listViewModel = NewListViewModel(host, () => purges++);

        await listViewModel.DeleteAllSessionsAsync();
        import?.Dispose();

        Assert.Equal(1, host.Confirmations);
        Assert.Equal(0, purges);
        Assert.Equal(("Cannot Delete", expected), Assert.Single(host.MessagesShown));
        Assert.Same(session, Assert.Single(host.LoggingSessions));
    }

    /// <summary>
    /// #682 review, the other order: the purge starts first. While it runs, an import (here, one
    /// started the moment the purge begins, as the bell's import was) cannot claim the store. When
    /// the purge ends, here by failing, the store is free again.
    /// </summary>
    [Fact]
    public async Task DeleteAll_HoldsTheStoreUntilThePurgeEnds_SoNoImportCanStartDuringIt()
    {
        var host = new Host(new LoggingSession(0, "Session_0"));
        var importDuringPurge = "not tried";
        var listViewModel = NewListViewModel(host, () =>
        {
            using var claim = host.SessionStore.TryBeginImport();
            importDuringPurge = claim is null ? "refused" : "claimed";
        });

        await listViewModel.DeleteAllSessionsAsync();

        Assert.Equal("refused", importDuringPurge);
        using var after = host.SessionStore.TryBeginImport();
        Assert.NotNull(after);
    }

    /// <summary>
    /// The control: with no import and no logging, DELETE ALL asks once and goes on to the purge.
    /// Without it, a gate that refused always would pass the tests above.
    /// </summary>
    [Fact]
    public async Task DeleteAll_WithNoImport_GoesOnToThePurge()
    {
        var host = new Host(new LoggingSession(0, "Session_0"));
        var purges = 0;
        var listViewModel = NewListViewModel(host, () => purges++);

        await listViewModel.DeleteAllSessionsAsync();

        Assert.Equal(1, host.Confirmations);
        Assert.Equal(1, purges);
        Assert.DoesNotContain(host.MessagesShown, m => m.Title == "Cannot Delete");
    }

    /// <summary>
    /// A list view model whose purge runs <paramref name="onPurge"/>, then stops: the factory
    /// resolver is the purge's first step, so it throws once called and the purge goes no further.
    /// Its "Delete Failed" message is what the control test ignores.
    /// </summary>
    private static LoggingSessionListViewModel NewListViewModel(Host host, Action onPurge) =>
        new(
            host,
            () =>
            {
                onPurge();
                throw new InvalidOperationException("test: the purge stops here");
            },
            Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "delete-all-682", "DAQiFiDatabase.db"),
            new RecordingAppLogger());

    private sealed class Host : ILoggingSessionListHost
    {
        internal Host(params LoggingSession[] sessions) =>
            LoggingSessions = new ObservableCollection<LoggingSession>(sessions);

        internal List<(string Title, string Message)> MessagesShown { get; } = [];

        internal int Confirmations { get; private set; }

        internal int Suspends { get; private set; }

        /// <summary>Runs while the confirmation is open, before the user answers DELETE ALL.</summary>
        internal Action? WhileConfirming { get; set; }

        public ObservableCollection<LoggingSession> LoggingSessions { get; }

        public LoggingSession SelectedLoggingSession { set { } }

        public bool IsLoggedDataBusy { set { } }

        public string LoggedDataBusyReason { set { } }

        public bool IsLoggingActive { get; set; }

        public SessionStoreGate SessionStore { get; } = new();

        public void NotifyLoggingSessionsChanged() { }

        public void DisplaySessionOnPlot(LoggingSession session) { }

        public void DeleteSessionFromDatabase(LoggingSession session) { }

        public void ClearPlot() { }

        public void SuspendConsumer() => Suspends++;

        public void ResumeConsumer() { }

        public void ClearBuffer() { }

        public void DiscardPendingBatch() { }

        public Task ShowExportDialogForSessionAsync(int sessionId) => Task.CompletedTask;

        public Task ShowExportDialogForSessionsAsync(IReadOnlyList<LoggingSession> sessions) => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message, string affirmativeLabel, bool isDestructive)
        {
            Confirmations++;
            WhileConfirming?.Invoke();
            return Task.FromResult(true);
        }

        public Task ShowMessageAsync(string title, string message)
        {
            MessagesShown.Add((title, message));
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// #682 review, the production half: the import of sessions from an earlier version (the bell's
/// Import) claims the shell's <see cref="SessionStoreGate"/>, the one DELETE ALL claims through
/// <see cref="ILoggingSessionListHost.SessionStore"/>. Both orders: DELETE ALL holds the store, so
/// the import does not start and keeps its offer; the import runs, so DELETE ALL cannot claim it.
/// </summary>
[Collection(AppHostCollection.Name)]
public sealed class DeleteAllImportHostTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "delete-all-import-" + Guid.NewGuid().ToString("N"));

    private string Old => Path.Combine(_root, "ProgramData", "DAQiFi");

    private string Current => Path.Combine(_root, "LocalAppData", "DAQiFi");

    public DeleteAllImportHostTests()
    {
        Directory.CreateDirectory(Old);
        Directory.CreateDirectory(Current);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task TheEarlierVersionImport_WhileDeleteAllHoldsTheStore_DoesNotStartAndKeepsItsOffer()
    {
        var viewModel = NewViewModel();
        var started = false;
        var import = Import(() => { started = true; return Manager(); });

        using (var purge = ((ILoggingSessionListHost)viewModel).SessionStore.TryBeginPurge())
        {
            Assert.NotNull(purge);
            await viewModel.ImportPreviousSessionsAsync(import);
        }

        Assert.False(started);
        Assert.False(viewModel.IsImportingPreviousSessions);
        var offer = Assert.Single(viewModel.NotificationList);
        Assert.Equal(SessionStoreGate.WaitForDeleteAll, offer.Message);
        Assert.Equal("Import", offer.ActionText);
    }

    [Fact]
    public async Task TheEarlierVersionImport_HoldsTheStoreWhileItRuns_SoDeleteAllCannotClaimIt()
    {
        var viewModel = NewViewModel();
        var store = ((ILoggingSessionListHost)viewModel).SessionStore;
        var purgeDuringImport = "not tried";
        var import = Import(() =>
        {
            using var claim = store.TryBeginPurge();
            purgeDuringImport = claim is null ? "refused" : "claimed";
            return Manager();
        });

        await viewModel.ImportPreviousSessionsAsync(import);

        Assert.Equal("refused", purgeDuringImport);
        using var after = store.TryBeginPurge();
        Assert.NotNull(after);
    }

    private static DaqifiViewModel NewViewModel()
    {
        Daqifi.Desktop.App.InitializeMobile();
        return new DaqifiViewModel(new NullDialogService());
    }

    /// <summary>An import with nothing to copy: no earlier database and no saved profiles. The
    /// <paramref name="profiles"/> callback runs on the UI thread as the import starts.</summary>
    private PreviousVersionImport Import(Func<LoggingManager> profiles) => new(
        new PreviousSessionImport(null, Path.Combine(Current, "DAQiFiDatabase.db"), Current),
        new PreviousSettingsImport(Old, new RecordingAppLogger()),
        profiles,
        () => new DaqifiSettings(Path.Combine(Current, PreviousSettingsImport.SettingsFileName)));

    private LoggingManager Manager() =>
        new(new UnusedContextFactory(), Path.Combine(Current, PreviousSettingsImport.ProfilesFileName));

    private sealed class UnusedContextFactory : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() =>
            throw new InvalidOperationException("Profile persistence must not touch the database.");
    }
}
