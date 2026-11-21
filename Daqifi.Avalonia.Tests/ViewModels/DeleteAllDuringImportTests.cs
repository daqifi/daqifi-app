using System.Collections.ObjectModel;
using Daqifi.Desktop.DialogService;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// #682: DELETE ALL was accepted while an SD card IMPORT ALL ran. The purge moves the database file
/// aside and builds an empty one, so the file the import was writing failed with "disk I/O error",
/// the result dialog blamed the device connection, and the import then wrote the rest of its files
/// into the new database, so the list that DELETE ALL emptied filled again.
/// </summary>
/// <remarks>
/// <para>The APP LOGS tab, and its DELETE ALL, are outside the DEVICE LOGS busy overlay, so the
/// click reaches <see cref="LoggingSessionListViewModel.DeleteAllSessionsAsync"/>. That method
/// refused only while logging. It now also refuses while the host reports an import, in the same way
/// and with the same title.</para>
/// <para>The purge is the thing that must not run. These tests resolve the logging-context factory
/// through a counter that throws: the purge resolves it first, so a count of zero means that nothing
/// on disk was touched.</para>
/// </remarks>
public class DeleteAllDuringImportTests
{
    private const string WaitForImport = "Wait for the import to finish before you delete all sessions.";

    /// <summary>The issue's steps: an import runs, the user clicks DELETE ALL.</summary>
    [Fact]
    public async Task DeleteAll_WhileAnImportRuns_IsRefusedAndTheSessionsStay()
    {
        var session = new LoggingSession(0, "SD Import - log_1");
        var host = new Host(session) { IsImportActive = true };
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
    /// earlier version, can start from outside it while it is open, so the gate is read again after
    /// the user answers DELETE ALL.
    /// </summary>
    [Theory]
    [InlineData(false, "Wait for the import to finish before you delete all sessions.")]
    [InlineData(true, "Please stop logging before deleting all sessions.")]
    public async Task DeleteAll_WhenWorkStartsWhileTheConfirmationIsOpen_IsRefused(bool logging, string expected)
    {
        var session = new LoggingSession(0, "Session_0");
        var host = new Host(session);
        host.WhileConfirming = () =>
        {
            if (logging) { host.IsLoggingActive = true; } else { host.IsImportActive = true; }
        };
        var purges = 0;
        var listViewModel = NewListViewModel(host, () => purges++);

        await listViewModel.DeleteAllSessionsAsync();

        Assert.Equal(1, host.Confirmations);
        Assert.Equal(0, purges);
        Assert.Equal(("Cannot Delete", expected), Assert.Single(host.MessagesShown));
        Assert.Same(session, Assert.Single(host.LoggingSessions));
    }

    /// <summary>
    /// The control: with no import and no logging, DELETE ALL asks once and goes on to the purge.
    /// Without it, a gate that refused always would pass the two tests above.
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
    /// A list view model whose purge counts, then stops: the factory resolver is the purge's first
    /// step, so it throws once counted and the purge goes no further. Its "Delete Failed" message
    /// is what the control test ignores.
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

        public bool IsImportActive { get; set; }

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
/// #682, the production half: what <see cref="DaqifiViewModel"/> tells the list view model. The
/// DEVICE LOGS busy count and the import of sessions from an earlier version are the two imports
/// that can run while DELETE ALL can be clicked. (IMPORT SD LOG puts the Logged Data busy overlay
/// over DELETE ALL while it runs.)
/// </summary>
[Collection(AppHostCollection.Name)]
public class DeleteAllImportHostTests
{
    [Fact]
    public void ReportsAnImport_WhileDeviceLogsIsBusyOrEarlierSessionsAreImported()
    {
        Daqifi.Desktop.App.InitializeMobile();
        var viewModel = new DaqifiViewModel(new NullDialogService());
        ILoggingSessionListHost host = viewModel;
        Assert.False(host.IsImportActive);

        // The app creates the pane only in a started host; a test puts one in place.
        var deviceLogs = new DeviceLogsViewModel(new RecordingAppLogger(), importer: null, marshalToUiThread: a => a());
        typeof(DaqifiViewModel).GetProperty(nameof(DaqifiViewModel.DeviceLogsViewModel))!
            .SetValue(viewModel, deviceLogs);
        Assert.False(host.IsImportActive);

        deviceLogs.BeginBusy();
        Assert.True(host.IsImportActive);
        deviceLogs.EndBusy();
        Assert.False(host.IsImportActive);

        viewModel.IsImportingPreviousSessions = true;
        try
        {
            Assert.True(host.IsImportActive);
        }
        finally
        {
            viewModel.IsImportingPreviousSessions = false;
        }
        Assert.False(host.IsImportActive);
    }
}
