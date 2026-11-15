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
    /// The review case: Notifications open, then SETTINGS. The pane opens on
    /// <see cref="DaqifiViewModel.IsAnyDrawerOpen"/>, so it must never read false while one panel
    /// replaces another. The old MultiBinding heard Notifications close before Settings open, the
    /// pane began to close, and the dismiss handler then closed Settings too. Every value the pane
    /// would receive during the switch is recorded here, in the order the binding gets them.
    /// </summary>
    [Theory]
    [InlineData("Notifications", "Settings")]
    [InlineData("Settings", "Notifications")]
    [InlineData("PlotSettings", "Settings")]
    [InlineData("Settings", "LogSummary")]
    [InlineData("Notifications", "PlotSettings")]
    public void A_panel_switch_never_reports_all_closed(string from, string to)
    {
        var vm = NewViewModel();
        OpenPanel(vm, from);
        var seen = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DaqifiViewModel.IsAnyDrawerOpen))
            {
                seen.Add(vm.IsAnyDrawerOpen);
            }
        };

        OpenPanel(vm, to);

        Assert.NotEmpty(seen);
        Assert.All(seen, Assert.True);
        Assert.Equal([to], Open(vm));
    }

    /// <summary>
    /// Each of the four flags drives the pane on its own: setting it raises
    /// <see cref="DaqifiViewModel.IsAnyDrawerOpen"/> and makes it true, and clearing it makes it false
    /// again. A flag missing from the notifications would leave the pane shut for that panel.
    /// </summary>
    [Theory]
    [InlineData("Settings")]
    [InlineData("Notifications")]
    [InlineData("PlotSettings")]
    [InlineData("LogSummary")]
    public void Each_panel_flag_opens_and_closes_the_pane(string panel)
    {
        var vm = NewViewModel();
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DaqifiViewModel.IsAnyDrawerOpen))
            {
                raised++;
            }
        };

        SetFlag(vm, panel, true);
        Assert.True(vm.IsAnyDrawerOpen);
        SetFlag(vm, panel, false);

        Assert.False(vm.IsAnyDrawerOpen);
        Assert.True(raised >= 2, $"{panel}: IsAnyDrawerOpen raised {raised} time(s), expected 2.");
    }

    /// <summary>
    /// Escape from outside the pane (the review case: focus still on the SETTINGS button).
    /// MainWindow.OnKeyDown calls <see cref="DaqifiViewModel.DismissDrawer"/>; it closes the open
    /// panel and reports true, so the key is consumed, and reports false with nothing open, so
    /// Escape still reaches whatever else wants it.
    /// </summary>
    [Fact]
    public void DismissDrawer_closes_the_open_panel_and_says_whether_it_did()
    {
        var vm = NewViewModel();
        Assert.False(vm.DismissDrawer());

        vm.IsAppSettingsOpen = true;

        Assert.True(vm.DismissDrawer());
        Assert.Empty(Open(vm));
        Assert.False(vm.DismissDrawer());
    }

    private static void OpenPanel(DaqifiViewModel vm, string panel)
    {
        switch (panel)
        {
            case "Settings": vm.IsAppSettingsOpen = true; break; // the top-bar toggle is two-way
            case "Notifications": vm.OpenNotificationsCommand.Execute(null); break;
            case "PlotSettings": vm.OpenLiveGraphSettingsCommand.Execute(null); break;
            default: vm.OpenLogSummaryCommand.Execute(null); break;
        }
    }

    private static void SetFlag(DaqifiViewModel vm, string panel, bool value)
    {
        switch (panel)
        {
            case "Settings": vm.IsAppSettingsOpen = value; break;
            case "Notifications": vm.IsNotificationsOpen = value; break;
            case "PlotSettings": vm.IsLiveGraphSettingsOpen = value; break;
            default: vm.IsLogSummaryOpen = value; break;
        }
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
