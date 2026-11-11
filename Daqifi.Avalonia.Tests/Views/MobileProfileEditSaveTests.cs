using System.Text.RegularExpressions;
using Daqifi.Avalonia.Views.Mobile;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #560: on the phone, a profile edit left open in the drawer was lost when the user changed
/// tabs and the app then closed.
///
/// <para>
/// The phone shell builds its Profiles page once and keeps it, so a tab change only hides it, and
/// Android stops a background app without detaching anything. On desktop the same two exits detach
/// the pane, and <see cref="ProfilesPaneViewModel.Cleanup"/> saves the edit (#541, #542). The phone
/// had no equivalent, so the list showed the edit as saved and the next launch did not have it.
/// </para>
///
/// <para>
/// Two halves. The first tests <see cref="MobileProfileEdit.SaveOnLeave"/>, the save the shell
/// calls, against the real profiles file (the production <c>LoggingManager.Instance</c>, in the
/// throwaway data directory the assembly's module initializer sets) and against a writer that
/// fails. The second reads <c>MobileMainView.axaml.cs</c> and pins the four places that call it: a
/// tab change, the app going to the background, and the shell leaving the visual tree. This test
/// project cannot host an Avalonia control (no lifetime, so <c>Dispatcher.UIThread</c> is
/// unusable), and a call that goes away is the whole of the defect, so the call sites are guarded
/// as source, the way <see cref="StorageMobilePivotRefreshTests"/> guards #463.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class MobileProfileEditSaveTests : IDisposable
{
    private const string Shell = "Daqifi.Avalonia/Views/MobileMainView.axaml.cs";

    private readonly List<ProfilesPaneViewModel> _panes = [];

    public MobileProfileEditSaveTests() => Daqifi.Desktop.App.InitializeMobile();

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

    /// <summary>A saved profile at 100 Hz, in the file and in the list, as the phone finds it.</summary>
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
                    DeviceSerialNo = "MOBILE-EDIT-1",
                    MacAddress = string.Empty,
                    SamplingFrequency = 100,
                    Channels = [],
                },
            ],
        };
        Assert.True(LoggingManager.Instance.SubscribeProfile(profile));
        return profile;
    }

    /// <summary>What the next launch sees: the profile as the file holds it.</summary>
    private static Profile OnDisk(Profile profile) =>
        LoggingManager.Instance.LoadProfilesFromXml().Single(p => p.ProfileId == profile.ProfileId);

    /// <summary>
    /// The issue's steps: EDIT, change the name and the frequency, then leave the page with the
    /// drawer open. The file must have both changes, and there is nothing to report.
    /// </summary>
    [Fact]
    public void Leaving_with_the_edit_drawer_open_saves_the_edit_to_the_file()
    {
        var profile = Saved("Bench rig");
        var pane = Pane();
        pane.OpenEditDrawerCommand.Execute(profile);
        profile.Name = "EDITED-ON-PHONE";
        profile.Devices[0].SamplingFrequency = 7;

        var unsaved = MobileProfileEdit.SaveOnLeave(pane);

        Assert.Null(unsaved);
        var saved = OnDisk(profile);
        Assert.Equal("EDITED-ON-PHONE", saved.Name);
        Assert.Equal(7, saved.Devices[0].SamplingFrequency);
    }

    /// <summary>
    /// After a good save the drawer is closed, as the close button leaves it: the user comes back
    /// to the list, which now matches the file.
    /// </summary>
    [Fact]
    public void A_saved_edit_closes_the_drawer()
    {
        var profile = Saved("Bench rig");
        var pane = Pane();
        pane.OpenEditDrawerCommand.Execute(profile);
        profile.Name = "Bench rig renamed";

        MobileProfileEdit.SaveOnLeave(pane);

        Assert.False(pane.IsDrawerOpen);
        Assert.Null(pane.SelectedProfile);
    }

    /// <summary>A pane whose profile writes are recorded and succeed or fail on demand.</summary>
    private sealed class Writer
    {
        public bool Succeeds { get; set; } = true;
        public List<string> Written { get; } = [];

        public bool Write(Profile profile)
        {
            Written.Add(profile.Name);
            return Succeeds;
        }
    }

    private ProfilesPaneViewModel Pane(Writer writer)
    {
        var pane = new ProfilesPaneViewModel(writer.Write);
        _panes.Add(pane);
        return pane;
    }

    private static Profile Unsaved(string name) =>
        new() { Name = name, ProfileId = Guid.NewGuid(), CreatedOn = DateTime.Now, Devices = [] };

    /// <summary>
    /// The write fails. The edit must not be dropped: the drawer stays open on the same profile
    /// with the reason, and the shell gets a sentence for the phone's notifications that carries
    /// the same reason and says where the edit is.
    /// </summary>
    [Fact]
    public void A_failed_save_keeps_the_edit_open_and_returns_the_reason()
    {
        var writer = new Writer { Succeeds = false };
        var pane = Pane(writer);
        var profile = Unsaved("Bench rig");
        pane.OpenEditDrawerCommand.Execute(profile);
        profile.Name = "Bench rig renamed";

        var unsaved = MobileProfileEdit.SaveOnLeave(pane);

        Assert.Equal(["Bench rig renamed"], writer.Written);
        Assert.True(pane.IsDrawerOpen);
        Assert.Same(profile, pane.SelectedProfile);
        Assert.True(pane.HasDrawerError);
        Assert.Equal(pane.DrawerError + MobileProfileEdit.StillOpenSuffix, unsaved);
    }

    /// <summary>
    /// The new-profile form has nothing to save until SAVE PROFILE, and the close button would
    /// throw it away. A tab change must leave it open as the user left it, and write nothing.
    /// </summary>
    [Fact]
    public void Leaving_on_the_new_profile_form_keeps_the_form_and_writes_nothing()
    {
        var writer = new Writer();
        var pane = Pane(writer);
        pane.OpenNewDrawerCommand.Execute(null);
        pane.NewProfileName = "Half-typed rig";

        var unsaved = MobileProfileEdit.SaveOnLeave(pane);

        Assert.Null(unsaved);
        Assert.True(pane.IsDrawerOpen);
        Assert.True(pane.IsNewProfile);
        Assert.Equal("Half-typed rig", pane.NewProfileName);
        Assert.Empty(writer.Written);
    }

    /// <summary>With the drawer closed there is no edit: nothing is written and nothing reported.</summary>
    [Fact]
    public void Leaving_with_the_drawer_closed_writes_nothing()
    {
        var writer = new Writer { Succeeds = false };
        var pane = Pane(writer);

        Assert.Null(MobileProfileEdit.SaveOnLeave(pane));
        Assert.Empty(writer.Written);
        Assert.False(pane.IsDrawerOpen);
    }

    // ---- The call sites in the phone shell ----

    /// <summary>
    /// A tab change to Stream. The Stream tab is not a <c>SecondaryHost</c> page, so it has its own
    /// method; without the call here, Profiles then Stream is exactly the issue's repro.
    /// </summary>
    [Fact]
    public void Changing_to_the_stream_tab_saves_the_open_edit() =>
        AssertCalls("ShowStream", "SaveProfileEdit()",
            "the Stream tab hides the cached Profiles page with its drawer still open (#560)");

    /// <summary>A tab change to Storage or Channels, which go through <c>ShowSecondary</c>.</summary>
    [Fact]
    public void Changing_to_another_secondary_tab_saves_the_open_edit() =>
        AssertCalls("ShowSecondary", "SaveProfileEdit()",
            "Storage and Channels replace the cached Profiles page with its drawer still open (#560)");

    /// <summary>
    /// The app goes to the background: HOME, the app switcher, or a call. Android may then stop
    /// the process with no further signal, so this is the last point the edit can be saved.
    /// </summary>
    [Fact]
    public void Going_to_the_background_saves_the_open_edit()
    {
        AssertCalls("OnAttachedToVisualTree", "Deactivated += OnAppDeactivated",
            "the shell must hear when the app goes to the background (#560)");
        AssertCalls("OnAppDeactivated", "SaveProfileEdit()",
            "the operating system can stop a background app with no other signal (#560)");
    }

    /// <summary>
    /// The shell leaves the visual tree, as when its activity is destroyed. It also drops the
    /// background subscription there, so a shell that is gone is not kept alive by the lifetime.
    /// </summary>
    [Fact]
    public void Leaving_the_visual_tree_saves_the_open_edit_and_unsubscribes()
    {
        AssertCalls("OnDetachedFromVisualTree", "SaveProfileEdit()",
            "a destroyed activity takes the shell, and the open edit, with it (#560)");
        AssertCalls("OnDetachedFromVisualTree", "Deactivated -= OnAppDeactivated",
            "the lifetime outlives the shell and would keep it rooted");
    }

    /// <summary>
    /// The one method the call sites go through must save through <see cref="MobileProfileEdit"/>
    /// (the close button's path, not a second one) and put a failure in the phone's notifications.
    /// <c>AddOnce</c>, because every tab change retries a failed save and would stack copies.
    /// </summary>
    [Fact]
    public void The_shell_saves_through_the_close_button_path_and_reports_a_failure()
    {
        AssertCalls("SaveProfileEdit", "MobileProfileEdit.SaveOnLeave(",
            "the save must be the close button's, the same SaveOpenEdit desktop's Cleanup uses");
        AssertCalls("SaveProfileEdit", "_notifications.AddOnce(",
            "a failed save on a tab change has no drawer on screen to show it");
    }

    private static void AssertCalls(string method, string call, string why) =>
        Assert.True(
            BodyOf(method).Contains(call, StringComparison.Ordinal),
            $"MobileMainView.{method} must contain '{call}': {why}.");

    /// <summary>
    /// Returns the code-only body of a method, comments and string literals blanked, so a call
    /// written in a comment cannot satisfy the guard. Fails loudly when the method is missing or
    /// has an expression body.
    /// </summary>
    private static string BodyOf(string method)
    {
        var source = BindingFacts.CodeOnly(BindingFacts.Source(Shell));

        var declaration = Regex.Match(source, $@"\bvoid\s+{Regex.Escape(method)}\s*\(");
        Assert.True(declaration.Success, $"{Shell}: no method named {method}.");

        var open = source.IndexOf('{', declaration.Index + declaration.Length);
        Assert.True(open >= 0, $"{Shell}: {method} has no body.");
        // An expression body has no brace of its own, so the next '{' belongs to the NEXT member,
        // and the guard would silently read that member instead.
        Assert.False(
            source[(declaration.Index + declaration.Length)..open].Contains("=>", StringComparison.Ordinal),
            $"{Shell}: {method} is expression-bodied; give it a block body so this guard reads it.");

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
