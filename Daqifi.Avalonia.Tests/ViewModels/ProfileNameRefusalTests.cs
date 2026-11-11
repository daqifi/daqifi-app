using Daqifi.Core.Communication.Messages;
using Daqifi.Desktop;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Xunit;
using CoreAnalogChannel = Daqifi.Core.Channel.AnalogChannel;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;
using IStreamingDevice = Daqifi.Desktop.Device.IStreamingDevice;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #559: the profile drawer did not examine the name before it saved.
///
/// <para>
/// A <b>blank</b> name made the save return "nothing to save", so the drawer closed with no
/// message, the list showed a profile with no name, and every edit in the drawer (the name, the
/// frequency, the channels) was gone at the next launch. A name with a <b>control character</b>
/// (U+0001 from a paste) made <c>XDocument.Save</c> throw, and the drawer blamed the file
/// permission and the free disk space, so a retry could never succeed.
/// </para>
///
/// <para>
/// The pane now refuses both names where the drawer saves: the drawer stays open, the message is
/// about the name, and the edit stays in memory until the user corrects the name. The profiles
/// go through the production <c>LoggingManager.Instance</c>, whose file sits in the throwaway data
/// directory the assembly's module initializer sets, so "saved" below means "read back off disk".
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class ProfileNameRefusalTests : IDisposable
{
    private const string ControlCharacterName = "Rig\u0001A";

    private readonly List<ProfilesPaneViewModel> _panes = [];
    private readonly List<IStreamingDevice> _registered = [];

    public ProfileNameRefusalTests() => Daqifi.Desktop.App.InitializeMobile();

    public void Dispose()
    {
        foreach (var pane in _panes) pane.Cleanup();
        foreach (var profile in LoggingManager.Instance.SubscribedProfiles.ToList())
        {
            try { LoggingManager.Instance.UnsubscribeProfile(profile); } catch { /* best-effort cleanup */ }
        }
        foreach (var device in _registered)
        {
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); } catch { /* best-effort cleanup */ }
        }
    }

    private ProfilesPaneViewModel Pane()
    {
        var pane = new ProfilesPaneViewModel();
        _panes.Add(pane);
        return pane;
    }

    /// <summary>A saved profile with one device at 100 Hz, in the file and in the list.</summary>
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
                    DeviceSerialNo = "PROFILE-NAME-1",
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

    /// <summary>The words of the one message the drawer had before, for a failed file write.</summary>
    private static void AssertNotAboutTheFile(string message)
    {
        Assert.DoesNotContain("permission", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disk", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The bench report: change the frequency, clear the name, close the drawer. The drawer must
    /// stay open on the edit and say the name is the problem; nothing reaches the file.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Closing_the_drawer_on_a_blank_name_keeps_it_open_and_says_so(string blank)
    {
        var profile = Saved("Bench rig");
        var pane = Pane();
        pane.OpenEditDrawerCommand.Execute(profile);
        profile.Devices[0].SamplingFrequency = 7;
        profile.Name = blank;

        pane.CloseDrawerCommand.Execute(null);

        Assert.True(pane.IsDrawerOpen);
        Assert.Same(profile, pane.SelectedProfile);
        Assert.Equal(ProfilesPaneViewModel.ProfileNameBlankMessage, pane.DrawerError);
        Assert.Equal("Bench rig", OnDisk(profile).Name);
    }

    /// <summary>
    /// A control character cannot go into XML 1.0. The drawer must say the name holds a character
    /// it cannot save, not send the user to look for a permission or disk problem.
    /// </summary>
    [Fact]
    public void Closing_the_drawer_on_a_control_character_says_the_name_is_the_problem()
    {
        var profile = Saved("Bench rig");
        var pane = Pane();
        pane.OpenEditDrawerCommand.Execute(profile);
        profile.Name = ControlCharacterName;

        pane.CloseDrawerCommand.Execute(null);

        Assert.True(pane.IsDrawerOpen);
        Assert.Equal(ProfilesPaneViewModel.ProfileNameCharacterMessage, pane.DrawerError);
        AssertNotAboutTheFile(pane.DrawerError);
        Assert.Equal("Bench rig", OnDisk(profile).Name);
    }

    /// <summary>
    /// The edit is not lost: after the refusal the user types a name and closes again, and the
    /// file gets the new name AND the frequency changed before the refusal.
    /// </summary>
    [Fact]
    public void Correcting_a_refused_name_saves_the_whole_edit()
    {
        var profile = Saved("Bench rig");
        var pane = Pane();
        pane.OpenEditDrawerCommand.Execute(profile);
        profile.Devices[0].SamplingFrequency = 7;
        profile.Name = string.Empty;
        pane.CloseDrawerCommand.Execute(null);

        profile.Name = "Bench rig 7 Hz";
        pane.CloseDrawerCommand.Execute(null);

        Assert.False(pane.IsDrawerOpen);
        Assert.Equal(string.Empty, pane.DrawerError);
        var saved = OnDisk(profile);
        Assert.Equal("Bench rig 7 Hz", saved.Name);
        Assert.Equal(7, saved.Devices[0].SamplingFrequency);
    }

    /// <summary>
    /// The other direction: a valid name still saves and closes. The emoji is a surrogate pair,
    /// which a per-<c>char</c> XML check would refuse wrongly.
    /// </summary>
    [Theory]
    [InlineData("Bench rig renamed")]
    [InlineData("Rig \U0001F527 A")]
    public void A_valid_name_saves_and_closes(string name)
    {
        var profile = Saved("Bench rig");
        var pane = Pane();
        pane.OpenEditDrawerCommand.Execute(profile);
        profile.Name = name;

        pane.CloseDrawerCommand.Execute(null);

        Assert.False(pane.IsDrawerOpen);
        Assert.Equal(string.Empty, pane.DrawerError);
        Assert.Equal(name, OnDisk(profile).Name);
    }

    /// <summary>
    /// Mobile: EDIT on a different profile is a way out of the drawer too. It must not replace the
    /// profile whose name is refused.
    /// </summary>
    [Fact]
    public void Opening_a_different_profile_on_a_blank_name_keeps_the_open_one()
    {
        var a = Saved("Profile A");
        var b = Saved("Profile B");
        var pane = Pane();
        pane.OpenEditDrawerCommand.Execute(a);
        a.Name = string.Empty;

        pane.OpenEditDrawerCommand.Execute(b);

        Assert.Same(a, pane.SelectedProfile);
        Assert.Equal(ProfilesPaneViewModel.ProfileNameBlankMessage, pane.DrawerError);
    }

    /// <summary>
    /// Desktop: a different tab takes the drawer away, so it cannot stay open. Cleanup must give
    /// the app notification the real reason, not a file-write reason, and not nothing.
    /// </summary>
    [Theory]
    [InlineData("", ProfilesPaneViewModel.ProfileNameBlankMessage)]
    [InlineData(ControlCharacterName, ProfilesPaneViewModel.ProfileNameCharacterMessage)]
    public void Leaving_the_pane_on_a_refused_name_reports_the_name_problem(string name, string reason)
    {
        var profile = Saved("Bench rig");
        var pane = Pane();
        pane.OpenEditDrawerCommand.Execute(profile);
        profile.Name = name;

        _panes.Remove(pane);
        var unsaved = pane.Cleanup();

        Assert.Equal(ProfilesPaneViewModel.UnsavedNameMessage(reason), unsaved);
        AssertNotAboutTheFile(unsaved!);
        Assert.Equal("Bench rig", OnDisk(profile).Name);
    }

    /// <summary>A wrapper with no transport, enough for the new-profile form to list a device.</summary>
    private sealed class BenchDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message) { }
    }

    private void Connect(string serial)
    {
        var device = new BenchDevice { Name = serial, DeviceSerialNo = serial };
        device.DataChannels.Add(new AnalogChannel(device, new CoreAnalogChannel(0)) { Name = "AI0" });
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
    }

    /// <summary>The new-profile form with the one connected device and its channel selected.</summary>
    private ProfilesPaneViewModel NewProfileForm()
    {
        Connect("PROFILE-NAME-NEW");
        var pane = Pane();
        pane.OpenNewDrawerCommand.Execute(null);
        var item = pane.NewDeviceItems.Single();
        item.IsSelected = true;
        foreach (var channel in item.ChannelItems) channel.IsSelected = true;
        return pane;
    }

    /// <summary>SAVE PROFILE writes through the same <c>XDocument.Save</c>; the same refusal applies.</summary>
    [Fact]
    public void Saving_a_new_profile_with_a_control_character_says_the_name_is_the_problem()
    {
        var pane = NewProfileForm();
        pane.NewProfileName = "A\u0001";

        pane.SaveNewProfileCommand.Execute(null);

        Assert.True(pane.IsDrawerOpen);
        Assert.Equal(ProfilesPaneViewModel.ProfileNameCharacterMessage, pane.DrawerError);
        Assert.DoesNotContain(LoggingManager.Instance.SubscribedProfiles, p => p.Name == "A\u0001");
    }

    /// <summary>The other direction for SAVE PROFILE: a valid name saves and closes.</summary>
    [Fact]
    public void Saving_a_new_profile_with_a_valid_name_saves_and_closes()
    {
        var pane = NewProfileForm();
        pane.NewProfileName = "New rig";

        pane.SaveNewProfileCommand.Execute(null);

        Assert.False(pane.IsDrawerOpen);
        Assert.Contains(LoggingManager.Instance.SubscribedProfiles, p => p.Name == "New rig");
    }

    /// <summary>CAPTURE FROM CURRENT SETTINGS writes through the same <c>XDocument.Save</c> too.</summary>
    [Fact]
    public void Capturing_current_settings_with_a_control_character_says_the_name_is_the_problem()
    {
        var pane = NewProfileForm();
        pane.NewProfileName = "A\u0001";

        pane.SaveCurrentSettingsCommand.Execute(null);

        Assert.True(pane.IsDrawerOpen);
        Assert.Equal(ProfilesPaneViewModel.ProfileNameCharacterMessage, pane.DrawerError);
        Assert.DoesNotContain(LoggingManager.Instance.SubscribedProfiles, p => p.Name == "A\u0001");
    }

    /// <summary>
    /// The messages reach the user only through the drawer's error box, on both heads. Both views
    /// compile their bindings, so renaming <c>DrawerError</c> or <c>HasDrawerError</c> is already a
    /// build error (AVLN2000, measured on each view). What no build can see is the binding taken
    /// off the box: the pane would set the message and nobody would see it. This row pins that.
    /// </summary>
    [Theory]
    [InlineData("Daqifi.Avalonia/Daqifi.Desktop/View/ProfilesPane.axaml")]
    [InlineData("Daqifi.Avalonia/Views/Mobile/ProfilesMobileView.axaml")]
    public void Both_drawers_bind_the_error_box_the_messages_go_to(string view)
    {
        BindingFacts.AssertBinds(view, "Text=\"{Binding DrawerError}\"");
        BindingFacts.AssertBinds(view, "IsVisible=\"{Binding HasDrawerError}\"");
    }
}
