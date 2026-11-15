using System.Text.RegularExpressions;
using Daqifi.Avalonia.Views.Mobile;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #588: on Android, Back never closed an open panel. It always sent the app to the
/// background.
///
/// <para>
/// Two halves. The first tests <see cref="MobileBackNavigation"/>, which closes one item inside a
/// pane, against the real view models. The second reads <c>MobileMainView.axaml.cs</c> and pins the
/// subscription and the order of the shell's own steps. This test project cannot host an Avalonia
/// control (no lifetime, so <c>Dispatcher.UIThread</c> is unusable), so the shell is guarded as
/// source, the way <see cref="MobileProfileEditSaveTests"/> guards #560.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class MobileBackNavigationTests : IDisposable
{
    private const string Shell = "Daqifi.Avalonia/Views/MobileMainView.axaml.cs";

    private readonly List<ProfilesPaneViewModel> _panes = [];

    public MobileBackNavigationTests() => Daqifi.Desktop.App.InitializeMobile();

    public void Dispose()
    {
        foreach (var pane in _panes) pane.Cleanup();
        foreach (var profile in LoggingManager.Instance.SubscribedProfiles.ToList())
        {
            try { LoggingManager.Instance.UnsubscribeProfile(profile); } catch { /* best-effort cleanup */ }
        }
    }

    private ProfilesPaneViewModel Pane()
    {
        var pane = new ProfilesPaneViewModel();
        _panes.Add(pane);
        return pane;
    }

    private static Profile Saved(string name)
    {
        var profile = new Profile
        {
            Name = name,
            ProfileId = Guid.NewGuid(),
            CreatedOn = DateTime.Now,
            Devices =
            [
                new ProfileDevice
                {
                    DeviceName = "Nq1",
                    DevicePartName = "Nq1",
                    DeviceSerialNo = "MOBILE-BACK-1",
                    MacAddress = string.Empty,
                    SamplingFrequency = 100,
                    Channels = [],
                },
            ],
        };
        Assert.True(LoggingManager.Instance.SubscribeProfile(profile));
        return profile;
    }

    // ---- One item inside a pane ----

    [Fact]
    public async Task Back_cancels_an_open_confirm_overlay()
    {
        var overlay = new ConfirmOverlayViewModel();
        var answer = overlay.ShowAsync("Delete?", "Really?", "DELETE");

        Assert.True(MobileBackNavigation.Close(overlay));

        Assert.False(overlay.IsOpen);
        Assert.True(answer.IsCompleted);
        Assert.False(await answer);
    }

    [Fact]
    public void Back_with_no_confirm_overlay_open_closes_nothing() =>
        Assert.False(MobileBackNavigation.Close(new ConfirmOverlayViewModel()));

    /// <summary>The issue's NEW PROFILE case: Back closes the drawer, as its close button does.</summary>
    [Fact]
    public void Back_closes_the_new_profile_drawer()
    {
        var pane = Pane();
        pane.OpenNewDrawerCommand.Execute(null);
        Assert.True(pane.IsDrawerOpen);

        Assert.True(MobileBackNavigation.Close(pane));

        Assert.False(pane.IsDrawerOpen);
    }

    /// <summary>An edit closes through the close button's path, so the edit is saved (#541).</summary>
    [Fact]
    public void Back_closes_the_edit_drawer_and_saves_the_edit()
    {
        var profile = Saved("Bench rig");
        var pane = Pane();
        pane.OpenEditDrawerCommand.Execute(profile);
        profile.Name = "Renamed by Back";

        Assert.True(MobileBackNavigation.Close(pane));

        Assert.False(pane.IsDrawerOpen);
        Assert.Equal(
            "Renamed by Back",
            LoggingManager.Instance.LoadProfilesFromXml().Single(p => p.ProfileId == profile.ProfileId).Name);
    }

    /// <summary>The confirm overlay is above the drawer, so it closes first and the drawer stays.</summary>
    [Fact]
    public void Back_closes_the_profiles_confirm_overlay_before_the_drawer()
    {
        var pane = Pane();
        pane.OpenNewDrawerCommand.Execute(null);
        _ = pane.ConfirmOverlay.ShowAsync("Delete?", "Really?", "DELETE");

        Assert.True(MobileBackNavigation.Close(pane));

        Assert.False(pane.ConfirmOverlay.IsOpen);
        Assert.True(pane.IsDrawerOpen);
    }

    [Fact]
    public void Back_on_the_profile_list_closes_nothing() =>
        Assert.False(MobileBackNavigation.Close(Pane()));

    [Fact]
    public void Back_closes_the_channel_settings_drawer()
    {
        var pane = new ChannelsPaneViewModel { IsSettingsOpen = true };

        Assert.True(MobileBackNavigation.Close(pane));

        Assert.False(pane.IsSettingsOpen);
        Assert.False(MobileBackNavigation.Close(pane));
    }

    // ---- The shell ----

    /// <summary>
    /// The shell must listen to Back while it is on screen and stop when it leaves, so a shell that
    /// is gone is not kept alive by the top level.
    /// </summary>
    [Fact]
    public void The_shell_listens_to_back_and_stops_when_it_leaves()
    {
        Assert.Contains("BackRequested += OnBackRequested", BodyOf("OnAttachedToVisualTree"), StringComparison.Ordinal);
        Assert.Contains("BackRequested -= OnBackRequested", BodyOf("OnDetachedFromVisualTree"), StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", BodyOf("OnBackRequested"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The order of the steps: the shell's own overlays, then the visible pane, then Live Graph.
    /// With nothing open on Live Graph, CloseTopmost returns false before it reaches ShowStream, so
    /// Android moves the app to the background.
    /// </summary>
    [Fact]
    public void The_shell_closes_the_top_most_item_first()
    {
        var body = BodyOf("CloseTopmost");
        var steps = new[]
        {
            "SettingsOverlay.IsVisible = false",
            "NotificationsOverlay.IsVisible = false",
            "if (!SecondaryHost.IsVisible) { return false; }",
            "MobileBackNavigation.Close(profiles)",
            "MobileBackNavigation.Close(channels)",
            "MobileBackNavigation.Close(sessions)",
            "ShowStream()",
        };
        var at = -1;
        foreach (var step in steps)
        {
            var next = body.IndexOf(step, StringComparison.Ordinal);
            Assert.True(next > at, $"MobileMainView.CloseTopmost: '{step}' is missing or out of order.");
            at = next;
        }
    }

    private static string BodyOf(string method)
    {
        var source = BindingFacts.CodeOnly(BindingFacts.Source(Shell));
        var declaration = Regex.Match(source, $@"\b(void|bool)\s+{Regex.Escape(method)}\s*\(");
        Assert.True(declaration.Success, $"{Shell}: no method named {method}.");
        var open = source.IndexOf('{', declaration.Index + declaration.Length);
        Assert.True(open >= 0, $"{Shell}: {method} has no body.");
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; }
            else if (source[i] == '}' && --depth == 0) { return source[(open + 1)..i]; }
        }
        Assert.Fail($"{Shell}: {method}'s body never closes.");
        return string.Empty;
    }
}
