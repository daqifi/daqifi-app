using Daqifi.Desktop.DialogService;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// #519's sibling: a deleted session's ID is reused, and <see cref="LoggingSession.Equals"/>
/// compares IDs. Selecting the new session must not keep the deleted one selected.
/// </summary>
[Collection(AppHostCollection.Name)]
public class SelectedSessionReferenceTests
{
    [Fact]
    public void SelectingANewSessionWithAReusedId_ReplacesTheOldOne()
    {
        Daqifi.Desktop.App.InitializeMobile();
        var viewModel = new DaqifiViewModel(new NullDialogService());
        var deleted = new LoggingSession(2, "Session_2");
        var reused = new LoggingSession(2, "Session_2 (new run)");

        viewModel.SelectedLoggingSession = deleted;
        viewModel.SelectedLoggingSession = reused;

        Assert.Same(reused, viewModel.SelectedLoggingSession);
    }
}
