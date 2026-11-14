using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #541: the profile drawer saved an edit only when the user closed it.
///
/// <para>
/// The drawer binds straight to the <see cref="Profile"/> in memory, so an edit changes it at
/// once, and only <c>CloseDrawer</c> wrote it to the file. Two other ways out of the drawer did
/// not: on desktop, a different tab takes the pane out of the visual tree, which runs
/// <see cref="ProfilesPaneViewModel.Cleanup"/>; on mobile, the list stays live under the drawer,
/// so EDIT on another profile replaced the one in the drawer. Either way the list went on showing
/// the edit and the next launch did not have it.
/// </para>
///
/// <para>
/// The first two tests read the real profiles file back (the production
/// <c>LoggingManager.Instance</c>, in the throwaway data directory the assembly's module
/// initializer sets), so they prove the edit reached disk rather than that a method was called.
/// The rest give the pane a profile writer that records or fails, which is the only way to make a
/// write fail without breaking the shared data directory for every other test.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class ProfileDrawerLeaveSaveTests : IDisposable
{
    private readonly List<ProfilesPaneViewModel> _panes = [];

    public ProfileDrawerLeaveSaveTests() => Daqifi.Desktop.App.InitializeMobile();

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

    /// <summary>A saved profile, in the file and in the list, as the pane finds it.</summary>
    private static Profile Saved(string name)
    {
        var profile = new Profile
        {
            Name = name,
            ProfileId = Guid.NewGuid(),
            CreatedOn = DateTime.Now,
            Devices = [],
        };
        Assert.True(LoggingManager.Instance.SubscribeProfile(profile));
        return profile;
    }

    /// <summary>What the next launch sees: the profile as the file holds it.</summary>
    private static string NameOnDisk(Profile profile) =>
        LoggingManager.Instance.LoadProfilesFromXml().Single(p => p.ProfileId == profile.ProfileId).Name;

    /// <summary>
    /// Desktop: rename in the drawer, then click a different tab. The pane leaves the visual tree
    /// and its view calls <see cref="ProfilesPaneViewModel.Cleanup"/>. The file must have the new
    /// name.
    /// </summary>
    [Fact]
    public void Leaving_the_pane_with_the_drawer_open_saves_the_edit()
    {
        var profile = Saved("Before tab change");
        var pane = Pane();
        pane.OpenEditDrawerCommand.Execute(profile);
        profile.Name = "After tab change";

        _panes.Remove(pane);
        pane.Cleanup();

        Assert.Equal("After tab change", NameOnDisk(profile));
    }

    /// <summary>
    /// Mobile: rename profile A in the drawer, then tap EDIT on profile B in the list above it.
    /// A's edit must reach the file, and the drawer must then show B.
    /// </summary>
    [Fact]
    public void Opening_a_different_profile_saves_the_one_left_open()
    {
        var a = Saved("Profile A");
        var b = Saved("Profile B");
        var pane = Pane();
        pane.OpenEditDrawerCommand.Execute(a);
        a.Name = "Profile A renamed";

        pane.OpenEditDrawerCommand.Execute(b);

        Assert.Same(b, pane.SelectedProfile);
        Assert.Equal("Profile A renamed", NameOnDisk(a));
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
    /// The write fails while the pane is leaving. The drawer cannot stay open to say so, so
    /// Cleanup returns the sentence for an app notification, and it names the profile.
    /// </summary>
    [Fact]
    public void A_failed_save_when_leaving_the_pane_is_reported_with_the_profile_name()
    {
        var writer = new Writer();
        var pane = Pane(writer);
        var profile = Unsaved("Bench rig");
        pane.OpenEditDrawerCommand.Execute(profile);
        profile.Name = "Bench rig renamed";
        writer.Succeeds = false;

        _panes.Remove(pane);
        var unsaved = pane.Cleanup();

        Assert.Equal(["Bench rig renamed"], writer.Written);
        Assert.Equal(ProfilesPaneViewModel.UnsavedEditMessage("Bench rig renamed"), unsaved);
        Assert.Contains("'Bench rig renamed'", unsaved);
    }

    /// <summary>A save that works reports nothing; the pane leaves quietly.</summary>
    [Fact]
    public void A_successful_save_when_leaving_the_pane_reports_nothing()
    {
        var writer = new Writer();
        var pane = Pane(writer);
        pane.OpenEditDrawerCommand.Execute(Unsaved("Quiet"));

        _panes.Remove(pane);

        Assert.Null(pane.Cleanup());
        Assert.Equal(["Quiet"], writer.Written);
    }

    /// <summary>
    /// Leaving with the drawer closed, or on the new-profile form, has no edit to write: nothing
    /// is written and nothing is reported.
    /// </summary>
    [Fact]
    public void Leaving_with_no_edit_open_writes_nothing()
    {
        var writer = new Writer { Succeeds = false };
        var closed = Pane(writer);
        var newForm = Pane(writer);
        newForm.OpenNewDrawerCommand.Execute(null);

        _panes.Clear();

        Assert.Null(closed.Cleanup());
        Assert.Null(newForm.Cleanup());
        Assert.Empty(writer.Written);
    }

    /// <summary>
    /// Opening profile B while A's write fails must not replace A: the drawer stays on A with the
    /// reason, exactly as the close button does, so the edit is not dropped.
    /// </summary>
    [Fact]
    public void A_failed_save_keeps_the_open_profile_when_another_is_opened()
    {
        var writer = new Writer();
        var pane = Pane(writer);
        var a = Unsaved("A");
        pane.OpenEditDrawerCommand.Execute(a);
        a.Name = "A renamed";
        writer.Succeeds = false;

        pane.OpenEditDrawerCommand.Execute(Unsaved("B"));

        Assert.Same(a, pane.SelectedProfile);
        Assert.True(pane.IsDrawerOpen);
        Assert.True(pane.HasDrawerError);
        Assert.Equal(["A renamed"], writer.Written);
    }

    /// <summary>The same guard on ADD PROFILE, which would otherwise swap the edit for the new form.</summary>
    [Fact]
    public void A_failed_save_keeps_the_open_profile_when_the_new_profile_form_is_opened()
    {
        var writer = new Writer();
        var pane = Pane(writer);
        var a = Unsaved("A");
        pane.OpenEditDrawerCommand.Execute(a);
        writer.Succeeds = false;

        pane.OpenNewDrawerCommand.Execute(null);

        Assert.Same(a, pane.SelectedProfile);
        Assert.False(pane.IsNewProfile);
        Assert.True(pane.HasDrawerError);
    }

    /// <summary>EDIT on the profile already open is not a change of profile: nothing to save.</summary>
    [Fact]
    public void Opening_the_profile_already_open_writes_nothing()
    {
        var writer = new Writer();
        var pane = Pane(writer);
        var a = Unsaved("A");
        pane.OpenEditDrawerCommand.Execute(a);

        pane.OpenEditDrawerCommand.Execute(a);

        Assert.Same(a, pane.SelectedProfile);
        Assert.Empty(writer.Written);
    }
}
