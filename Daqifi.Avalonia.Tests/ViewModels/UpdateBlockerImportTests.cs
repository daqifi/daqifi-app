using Daqifi.Desktop.DialogService;
using Daqifi.Desktop.UpdateVersion;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// #513: an app update restarts the app, so it must wait while sessions from an earlier version
/// are being imported. Otherwise the restart ends the copy part way and the user imports again.
/// </summary>
[Collection(AppHostCollection.Name)]
public class UpdateBlockerImportTests
{
    private static DaqifiViewModel NewViewModel()
    {
        Daqifi.Desktop.App.InitializeMobile();
        return new DaqifiViewModel(new NullDialogService());
    }

    [Fact]
    public void ImportOfEarlierSessions_HoldsTheUpdate_UntilItEnds()
    {
        var viewModel = NewViewModel();
        Assert.Equal(UpdateBlocker.None, viewModel.CurrentUpdateBlocker());

        viewModel.IsImportingPreviousSessions = true;
        Assert.Equal(UpdateBlocker.LoggedDataBusy, viewModel.CurrentUpdateBlocker());

        viewModel.IsImportingPreviousSessions = false;
        Assert.Equal(UpdateBlocker.None, viewModel.CurrentUpdateBlocker());
    }

    /// <summary>
    /// The SD card pane is in the main window, so the window count cannot see a transfer in it.
    /// While the pane lists or imports the card's files, the update waits; when the last of
    /// overlapping transfers ends, it may go ahead (#503 review).
    /// </summary>
    [Fact]
    public void SdCardTransfer_HoldsTheUpdate_UntilTheLastTransferEnds()
    {
        var viewModel = NewViewModel();
        var deviceLogs = new DeviceLogsViewModel(new RecordingAppLogger(), importer: null, marshalToUiThread: a => a());
        // The app creates the pane only in a started host; a test puts one in place.
        typeof(DaqifiViewModel).GetProperty(nameof(DaqifiViewModel.DeviceLogsViewModel))!
            .SetValue(viewModel, deviceLogs);
        Assert.Equal(UpdateBlocker.None, viewModel.CurrentUpdateBlocker());

        deviceLogs.BeginBusy();     // an import starts
        deviceLogs.BeginBusy();     // a file listing starts
        Assert.Equal(UpdateBlocker.SdCardTransfer, viewModel.CurrentUpdateBlocker());

        deviceLogs.EndBusy();
        Assert.Equal(UpdateBlocker.SdCardTransfer, viewModel.CurrentUpdateBlocker());

        deviceLogs.EndBusy();
        Assert.Equal(UpdateBlocker.None, viewModel.CurrentUpdateBlocker());
    }

    /// <summary>
    /// An open profile editor saves only when it closes, so the restart would lose the edit.
    /// The update waits for it, and a pane that goes away with its editor open releases it.
    /// </summary>
    [Fact]
    public void OpenProfileEditor_HoldsTheUpdate_UntilItClosesOrThePaneGoes()
    {
        var viewModel = NewViewModel();
        var pane = new ProfilesPaneViewModel();
        var other = new ProfilesPaneViewModel();
        try
        {
            pane.IsDrawerOpen = true;
            other.IsDrawerOpen = true;
            Assert.Equal(UpdateBlocker.ProfileBeingEdited, viewModel.CurrentUpdateBlocker());

            pane.IsDrawerOpen = false;
            Assert.Equal(UpdateBlocker.ProfileBeingEdited, viewModel.CurrentUpdateBlocker());

            other.Cleanup();
            Assert.Equal(UpdateBlocker.None, viewModel.CurrentUpdateBlocker());
        }
        finally
        {
            pane.Cleanup();
            other.Cleanup();
        }
    }
}
