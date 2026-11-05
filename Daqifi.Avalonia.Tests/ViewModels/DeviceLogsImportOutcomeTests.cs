using System.Xml.Linq;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.Services;
using Daqifi.Desktop.ViewModels;
using Xunit;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// What a finished SD card import leaves behind for a head that cannot show a dialog (issue #521).
///
/// <para>
/// The defect these pin was not a missing message. <c>HandleImportFailure</c> already composed a
/// good sentence and routed it to the log; the one surface that showed it to a user was
/// <c>IMessageBoxService</c>, and <c>AvaloniaMessageBoxService</c> opens a <c>MessageDialog</c> —
/// a <c>Window</c> owned by <c>IClassicDesktopStyleApplicationLifetime.MainWindow</c>. Under the
/// single-view lifetime Android and iOS run there is no window to own one, so <c>ShowAsync</c>
/// returns <c>MessageBoxResult.None</c> and draws nothing. Measured on a Galaxy A16: polling the
/// accessibility tree across a whole failing import, the only transient text that ever appeared
/// was the progress line. An import that failed, one that succeeded and one that did nothing were
/// indistinguishable.
/// </para>
///
/// <para>
/// So the outcome is now state as well as a dialog, and these assert the two properties of that
/// arrangement a reader has to be able to trust: the inline notice carries the SAME sentence the
/// dialog does (it is not a second message-building path that can drift from the first), and it is
/// set whether or not a dialog can be shown.
/// </para>
///
/// <para>
/// The <c>DescribeImport</c> facts drive the pure describer rather than the command, and
/// deliberately so: the persisted-session branch is unreachable from this project, because
/// <c>AddImportedSession</c> reaches <c>LoggingManager.Instance</c> through
/// <c>Dispatcher.UIThread.Invoke</c> and nothing here builds an Avalonia lifetime — see
/// <see cref="DeviceLogsImportBatchTests"/>' remarks. A successful import's confirmation is the
/// half of #521 that had no on-screen trace at all, so leaving it untested was not an option;
/// splitting the wording out of the command is what makes it reachable.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class DeviceLogsImportOutcomeTests : IDisposable
{
    private const string TheFile = "LOG_0001.bin";

    private readonly List<IStreamingDevice> _registered = [];
    private readonly RecordingMessageBox _dialogs = new();
    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        foreach (var device in _registered)
        {
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); }
            catch { /* best-effort cleanup */ }
        }
    }

    #region A failure reaches a head with no dialog
    /// <summary>
    /// The headline fact of #521: after a failed import there is something on screen. Before this,
    /// every property a mobile view binds was exactly as it had been before the tap.
    /// </summary>
    [Fact]
    public async Task A_failed_import_leaves_the_failure_on_screen()
    {
        var (vm, importer) = await PaneWithOneFile();
        importer.Failures[TheFile] = new SdCardEmptyTransferException(TheFile);

        await vm.ImportFileCommand.ExecuteAsync(vm.DeviceFiles[0]);

        Assert.True(vm.HasImportOutcome);
        Assert.True(vm.ImportOutcomeIsFailure);
        Assert.Contains(TheFile, vm.ImportOutcomeMessage);
    }

    /// <summary>
    /// And it is the message the app had already composed, not a new one written for this surface.
    /// Pinned against the classifier's own constant rather than a literal: the point is that the
    /// notice reports what <c>SdCardFailureClassifier</c> decided, so a reworded constant must not
    /// be able to leave the two surfaces saying different things.
    /// </summary>
    [Fact]
    public async Task The_notice_carries_the_guidance_the_classifier_produced()
    {
        var (vm, importer) = await PaneWithOneFile();
        importer.Failures[TheFile] = new SdCardEmptyTransferException(TheFile);

        await vm.ImportFileCommand.ExecuteAsync(vm.DeviceFiles[0]);

        var expected = SdCardFailureClassifier.Classify(new SdCardEmptyTransferException(TheFile)).Guidance;
        Assert.Contains(expected, vm.ImportOutcomeMessage);
    }

    /// <summary>
    /// The anti-drift fact. One describer feeds both surfaces, so the dialog body and the inline
    /// notice are the same string — not merely similar. A future edit that words one of them in
    /// place fails here.
    /// </summary>
    [Fact]
    public async Task The_notice_and_the_dialog_say_the_same_thing()
    {
        var (vm, importer) = await PaneWithOneFile();
        // The exact failure #521 was measured on: a zero-byte log, which Core reports as a stall
        // with nothing received (daqifi-core#888 — a separate bug, and not one this depends on).
        importer.Failures[TheFile] = new SdCardTransferStalledException(
            TheFile, bytesReceived: 0, SdCardTransferStallReason.NoDataReceived, TimeSpan.FromSeconds(90));

        await vm.ImportFileCommand.ExecuteAsync(vm.DeviceFiles[0]);

        var dialog = Assert.Single(_dialogs.Shown);
        Assert.Equal("Import Failed", dialog.Caption);
        Assert.Equal(dialog.Text, vm.ImportOutcomeMessage);
    }

    /// <summary>
    /// A per-file failure must not paint the card-wide error panel — that is
    /// <c>HandleImportFailure</c>'s existing rule, and it is why the notice had to be a surface of
    /// its own rather than a reuse of <c>SdCardErrorMessage</c>. Binding the notice to the error
    /// panel instead would have hidden the file list (<c>HasFiles</c> needs <c>SdCardState.Ok</c>)
    /// over one empty log.
    /// </summary>
    [Fact]
    public async Task A_per_file_failure_reports_without_painting_the_card_wide_panel()
    {
        var (vm, importer) = await PaneWithOneFile();
        importer.Failures[TheFile] = new SdCardEmptyTransferException(TheFile);

        await vm.ImportFileCommand.ExecuteAsync(vm.DeviceFiles[0]);

        Assert.True(vm.HasImportOutcome);
        Assert.Equal(SdCardState.Ok, vm.SdCardState);
        Assert.False(vm.HasSdCardError);
        Assert.True(vm.HasFiles);
    }
    #endregion

    #region An import that finished says so
    /// <summary>
    /// The other half of #521: a successful import was equally silent. A 9,112-sample import on the
    /// phone produced no on-screen confirmation at all — only a row in the app log.
    /// </summary>
    [Fact]
    public void A_persisted_import_is_confirmed_with_what_it_imported()
    {
        var outcome = DeviceLogsViewModel.DescribeImport(TheFile, Persisted(samples: 9112));

        Assert.False(outcome.IsFailure);
        Assert.Contains(TheFile, outcome.Message);
        Assert.Contains("9,112 samples", outcome.Message);
    }

    /// <summary>One sample is one sample, not "1 samples".</summary>
    [Fact]
    public void A_single_sample_import_is_worded_in_the_singular()
    {
        var outcome = DeviceLogsViewModel.DescribeImport(TheFile, Persisted(samples: 1));

        Assert.Contains("1 sample.", outcome.Message);
        Assert.DoesNotContain("1 samples", outcome.Message);
    }

    /// <summary>
    /// A good import that still owes the user a word — readings that were NaN and were left out
    /// (#412) — keeps saying both things.
    /// </summary>
    [Fact]
    public void A_persisted_import_with_guidance_reports_both()
    {
        const string guidance = "Some readings could not be read and were left out.";
        var outcome = DeviceLogsViewModel.DescribeImport(TheFile, Persisted(samples: 10, guidance));

        Assert.Contains("10 samples", outcome.Message);
        Assert.Contains(guidance, outcome.Message);
        Assert.False(outcome.IsFailure);
    }

    /// <summary>
    /// An empty log produced no session, and the notice says which file and why — but it is NOT a
    /// failure. Nothing went wrong with the device or the app; an interrupted recording leaves such
    /// a file behind routinely, and a card full of them must not read as a card full of errors.
    /// </summary>
    [Fact]
    public async Task A_log_that_produced_no_session_is_reported_without_being_called_a_failure()
    {
        var (vm, _) = await PaneWithOneFile();

        await vm.ImportFileCommand.ExecuteAsync(vm.DeviceFiles[0]);

        Assert.True(vm.HasImportOutcome);
        Assert.False(vm.ImportOutcomeIsFailure);
        Assert.Contains(TheFile, vm.ImportOutcomeMessage);
        Assert.Contains(RecordingSessionImporter.NoSamplesGuidance, vm.ImportOutcomeMessage);
    }

    /// <summary>The Import All summary reaches the same surface — it was a dialog too.</summary>
    [Fact]
    public async Task The_import_all_summary_reaches_the_inline_notice_as_well()
    {
        var (vm, _) = await PaneWithOneFile();

        await vm.ImportAllFilesCommand.ExecuteAsync(null);

        var dialog = Assert.Single(_dialogs.Shown);
        Assert.Equal("Import Complete", dialog.Caption);
        Assert.Equal(dialog.Text, vm.ImportOutcomeMessage);
        Assert.False(vm.ImportOutcomeIsFailure);
    }
    #endregion

    #region The notice does not go stale
    /// <summary>
    /// Dismissable, because this notice is inline rather than modal: nothing else takes it off the
    /// screen once the user has read it.
    /// </summary>
    [Fact]
    public async Task The_notice_can_be_dismissed()
    {
        var (vm, importer) = await PaneWithOneFile();
        importer.Failures[TheFile] = new SdCardEmptyTransferException(TheFile);
        await vm.ImportFileCommand.ExecuteAsync(vm.DeviceFiles[0]);
        Assert.True(vm.HasImportOutcome);

        vm.ClearImportOutcomeCommand.Execute(null);

        Assert.False(vm.HasImportOutcome);
        Assert.False(vm.ImportOutcomeIsFailure);
        Assert.Equal(string.Empty, vm.ImportOutcomeMessage);
    }

    /// <summary>
    /// And the next import clears the previous one before it starts. A modal could not go stale —
    /// the user had dismissed it to get here; an inline notice can, and the failure of file A
    /// sitting beside a running import of file B is worse than no notice at all.
    /// </summary>
    [Fact]
    public async Task A_new_import_clears_the_previous_notice_before_it_runs()
    {
        var (vm, importer) = await PaneWithOneFile();
        importer.Failures[TheFile] = new SdCardEmptyTransferException(TheFile);
        await vm.ImportFileCommand.ExecuteAsync(vm.DeviceFiles[0]);
        Assert.True(vm.ImportOutcomeIsFailure);

        var noticeWhileRunning = "not observed";
        importer.Failures.Clear();
        importer.OnImport = _ => noticeWhileRunning = vm.ImportOutcomeMessage;

        await vm.ImportFileCommand.ExecuteAsync(vm.DeviceFiles[0]);

        Assert.Equal(string.Empty, noticeWhileRunning);
        Assert.False(vm.ImportOutcomeIsFailure);
    }
    #endregion

    #region The mobile view actually shows it
    /// <summary>
    /// The view-model half is useless on its own: #521 is a view-model that knew and a view that
    /// never asked. The compiler owns the member names in this file (<c>x:CompileBindings</c> is on
    /// — see <see cref="Views.DeviceLogsMobileViewBindingTests"/>); what it cannot see is the
    /// notice being deleted from the markup, which would put the pane back exactly where the issue
    /// found it with every other test still green.
    /// </summary>
    [Theory]
    [InlineData("HasImportOutcome")]
    [InlineData("ImportOutcomeMessage")]
    [InlineData("ImportOutcomeIsFailure")]
    [InlineData("ClearImportOutcomeCommand")]
    public void The_mobile_pane_binds_the_import_outcome(string member) =>
        Assert.Contains(
            $"{{Binding {member}}}",
            BindingFacts.Source("Daqifi.Avalonia/Views/Mobile/DeviceLogsMobileView.axaml"),
            StringComparison.Ordinal);

    /// <summary>
    /// And the size, on both heads. Parsed rather than searched: each of these views carries a
    /// comment that names <c>SizeDisplay</c>, so a whole-file substring check would be satisfied by
    /// the prose after the binding itself was gone.
    /// </summary>
    [Theory]
    [InlineData("Daqifi.Avalonia/Views/Mobile/DeviceLogsMobileView.axaml")]
    [InlineData("Daqifi.Avalonia/Daqifi.Desktop/View/DeviceLogsView.axaml")]
    public void Both_log_listings_show_the_file_size(string view)
    {
        var root = XDocument.Parse(BindingFacts.Source(view)).Root!;

        var bound = root.Descendants()
            .Where(e => e.Name.LocalName == "TextBlock")
            .Any(e => e.Attribute("Text")?.Value == "{Binding SizeDisplay}");

        Assert.True(bound, $"{view}: no TextBlock binds SizeDisplay, so the log listing does not " +
                           "show how big each file is (#521).");
    }
    #endregion

    #region Harness
    private async Task<(DeviceLogsViewModel Vm, RecordingSessionImporter Importer)> PaneWithOneFile()
    {
        var device = new RecordingStreamingDevice("AAAA0001")
        {
            SdCardFiles = [new SdCardFile { FileName = TheFile }],
        };
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);

        var importer = new RecordingSessionImporter();
        var vm = new DeviceLogsViewModel(new RecordingAppLogger(), importer, update =>
        {
            if (!_disposed)
            {
                update();
            }
        }, _dialogs);

        if (vm.InitialRefreshTask != null)
        {
            await vm.InitialRefreshTask;
        }

        Assert.Same(device, vm.SelectedDevice);
        Assert.Equal(SdCardState.Ok, vm.SdCardState);
        Assert.Single(vm.DeviceFiles);

        return (vm, importer);
    }

    /// <summary>
    /// A result standing for an import that landed a finished session — the branch the view model
    /// cannot be driven through here, which is why the wording is described by a pure method.
    /// </summary>
    private static SdCardImportResult Persisted(long samples, string guidance = "") =>
        new()
        {
            Session = new LoggingSession(),
            SamplesImported = samples,
            SessionPersisted = true,
            OutcomeGuidance = guidance,
            TimestampQuality = new ImportTimestampQuality(),
        };

    /// <summary>
    /// Records what it was asked for and can be told to fail a named file. A request it does not
    /// fail answers with a result carrying no persisted session.
    /// </summary>
    private sealed class RecordingSessionImporter : ISdCardSessionImporter
    {
        internal const string NoSamplesGuidance = "The log held no samples.";

        internal Dictionary<string, Exception> Failures { get; } = [];

        internal Action<string>? OnImport { get; set; }

        public Task<SdCardImportResult> ImportFromDeviceAsync(
            IStreamingDevice device,
            string fileName,
            IProgress<ImportProgress>? progress = null,
            CancellationToken ct = default)
        {
            OnImport?.Invoke(fileName);

            if (Failures.TryGetValue(fileName, out var failure))
            {
                return Task.FromException<SdCardImportResult>(failure);
            }

            return Task.FromResult(new SdCardImportResult
            {
                Session = new LoggingSession(),
                SamplesImported = 0,
                SessionPersisted = false,
                OutcomeGuidance = NoSamplesGuidance,
                TimestampQuality = new ImportTimestampQuality(),
            });
        }
    }

    /// <summary>Records every dialog instead of showing it.</summary>
    private sealed class RecordingMessageBox : IMessageBoxService
    {
        internal List<(string Caption, string Text)> Shown { get; } = [];

        public Task<MessageBoxResult> ShowAsync(
            string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
        {
            Shown.Add((caption, messageBoxText));
            return Task.FromResult(MessageBoxResult.OK);
        }
    }
    #endregion
}
