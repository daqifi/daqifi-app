using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #615: the four right-side panels — Settings, Notifications, Plot Settings and Log Summary —
/// share MainWindow's one SplitView pane. Two open at once would draw one on top of the other in that
/// pane, so opening one closes the others, and a dismiss from the view (a click on the scrim, or
/// Escape) closes whichever is open through <see cref="DaqifiViewModel.CloseDrawers"/>.
/// </summary>
[Collection(AppHostCollection.Name)]
public class DrawerExclusivityTests
{
    private static DaqifiViewModel NewViewModel()
    {
        Daqifi.Desktop.App.InitializeMobile();
        return new DaqifiViewModel(new NullDialogService());
    }

    private static string[] Open(DaqifiViewModel vm) =>
        new (string Name, bool IsOpen)[]
            {
                ("Settings", vm.IsAppSettingsOpen),
                ("Notifications", vm.IsNotificationsOpen),
                ("PlotSettings", vm.IsLiveGraphSettingsOpen),
                ("LogSummary", vm.IsLogSummaryOpen),
            }
            .Where(p => p.IsOpen)
            .Select(p => p.Name)
            .ToArray();

    [Fact]
    public void Opening_settings_closes_an_open_flyout()
    {
        var vm = NewViewModel();
        vm.OpenLogSummaryCommand.Execute(null);

        vm.IsAppSettingsOpen = true;

        Assert.Equal(["Settings"], Open(vm));
    }

    [Theory]
    [InlineData("Notifications")]
    [InlineData("PlotSettings")]
    [InlineData("LogSummary")]
    public void Opening_a_flyout_closes_settings(string flyout)
    {
        var vm = NewViewModel();
        vm.IsAppSettingsOpen = true;

        switch (flyout)
        {
            case "Notifications": vm.OpenNotificationsCommand.Execute(null); break;
            case "PlotSettings": vm.OpenLiveGraphSettingsCommand.Execute(null); break;
            default: vm.OpenLogSummaryCommand.Execute(null); break;
        }

        Assert.Equal([flyout], Open(vm));
    }

    [Fact]
    public void Opening_notifications_closes_plot_settings()
    {
        var vm = NewViewModel();
        vm.OpenLiveGraphSettingsCommand.Execute(null);

        vm.OpenNotificationsCommand.Execute(null);

        Assert.Equal(["Notifications"], Open(vm));
    }

    /// <summary>
    /// The scrim and Escape path: MainWindow cancels the pane's own close while
    /// <see cref="DaqifiViewModel.IsAnyDrawerOpen"/> is true and calls CloseDrawers instead, so the
    /// flags and the screen agree. The Logged Data session drawer is not in this pane and stays open.
    /// </summary>
    [Fact]
    public void CloseDrawers_closes_the_open_panel_and_nothing_else()
    {
        var vm = NewViewModel();
        vm.IsLoggingSessionSettingsOpen = true;
        vm.OpenNotificationsCommand.Execute(null);
        Assert.True(vm.IsAnyDrawerOpen);

        vm.CloseDrawers();

        Assert.Empty(Open(vm));
        Assert.False(vm.IsAnyDrawerOpen);
        Assert.True(vm.IsLoggingSessionSettingsOpen);
    }
}
