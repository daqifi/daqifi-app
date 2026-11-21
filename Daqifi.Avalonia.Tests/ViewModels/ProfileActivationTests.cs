using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
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
/// Issue #520: saving a profile and then activating it did not apply it.
///
/// <para>
/// The save, the XML round trip, the device match and the rate were all fine; the second test
/// below passes on the unfixed code and pins that. What was wrong is that activation only ever
/// ADDED the profile's channels. <c>AbstractStreamingDevice.AddChannels</c> writes the ADC enable
/// as one whole mask built from the channels it is given, so the board was switched to exactly the
/// profile's inputs, while every channel the user had switched on since the capture stayed active
/// and subscribed in the app. The Channels pane, the plot and the logger went on showing channels
/// the board had just been told to stop sending, which reads on the bench as "the profile did not
/// take".
/// </para>
///
/// <para>
/// The other two tests cover the issue's second half: when activation cannot apply, the user is
/// told why in one short line instead of getting nothing (logging on) or an ACTIVE tile over a
/// device that was given nothing (no channel matched).
/// </para>
///
/// <para>
/// Devices are registered with the production <c>ConnectionManager.Instance</c>, which is what
/// the pane matches against, and the profiles go through the production
/// <c>LoggingManager.Instance</c>, whose profiles file sits in the throwaway data directory the
/// assembly's module initializer points <c>DAQIFI_DATA_DIR</c> at.
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class ProfileActivationTests : IDisposable
{
    private readonly List<IStreamingDevice> _registered = [];
    private readonly ProfilesPaneViewModel _pane;

    public ProfileActivationTests()
    {
        Daqifi.Desktop.App.InitializeMobile();
        _pane = new ProfilesPaneViewModel();
    }

    /// <summary>Both managers are process-wide singletons; leave nothing for the next test.</summary>
    public void Dispose()
    {
        try { LoggingManager.Instance.Active = false; } catch { /* best-effort cleanup */ }
        foreach (var profile in LoggingManager.Instance.SubscribedProfiles.ToList())
        {
            profile.IsProfileActive = false;
            try { LoggingManager.Instance.UnsubscribeProfile(profile); } catch { /* best-effort cleanup */ }
        }
        foreach (var device in _registered)
        {
            foreach (var channel in device.DataChannels.Where(c => c.IsActive).ToList())
            {
                try { LoggingManager.Instance.Unsubscribe(channel); } catch { /* best-effort cleanup */ }
            }
            try { ConnectionManager.Instance.UnregisterConnectedDevice(device); } catch { /* best-effort cleanup */ }
        }
        _pane.Cleanup();
    }

    /// <summary>A wrapper with no transport: what it would send to the board is recorded.</summary>
    private sealed class BenchDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        internal List<string> Sent { get; } = [];

        protected override void SendMessage(IOutboundMessage<string> message) => Sent.Add(message.Data);

        /// <summary>The last analog-input enable mask the board was sent.</summary>
        internal string LastAdcMask() =>
            Sent.Last(s => s.StartsWith("ENAble:VOLTage:DC ", StringComparison.Ordinal))["ENAble:VOLTage:DC ".Length..];
    }

    private BenchDevice Connect(string serial, int channelCount = 4, string macAddress = "", string? name = null)
    {
        var device = new BenchDevice { Name = name ?? serial, DeviceSerialNo = serial, MacAddress = macAddress };
        device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };
        device.Metadata.PartNumber = "Nq1";
        for (var i = 0; i < channelCount; i++)
        {
            device.DataChannels.Add(new AnalogChannel(device, new CoreAnalogChannel(i)) { Name = $"AI{i}" });
        }
        ConnectionManager.Instance.RegisterConnectedDevice(device);
        _registered.Add(device);
        return device;
    }

    private void Disconnect(IStreamingDevice device)
    {
        ConnectionManager.Instance.UnregisterConnectedDevice(device);
        _registered.Remove(device);
    }

    /// <summary>What the Channels pane does when a tile is switched on.</summary>
    private static void Enable(IStreamingDevice device, params string[] names)
    {
        foreach (var name in names)
        {
            var channel = device.DataChannels.Single(c => c.Name == name);
            device.AddChannel(channel);
            LoggingManager.Instance.Subscribe(channel);
        }
    }

    /// <summary>What the Channels pane does when a tile is switched off.</summary>
    private static void Disable(IStreamingDevice device, params string[] names)
    {
        foreach (var name in names)
        {
            var channel = device.DataChannels.Single(c => c.Name == name);
            LoggingManager.Instance.Unsubscribe(channel);
            device.RemoveChannel(channel);
        }
    }

    /// <summary>The CAPTURE FROM CURRENT SETTINGS button.</summary>
    private Profile CaptureCurrentSettings(string name)
    {
        var before = LoggingManager.Instance.SubscribedProfiles.ToList();
        _pane.NewProfileName = name;
        _pane.SaveCurrentSettingsCommand.Execute(null);
        return LoggingManager.Instance.SubscribedProfiles.Except(before).Single();
    }

    private Task Activate(Profile profile) => _pane.ActivateProfileCommand.ExecuteAsync(profile);

    private static string[] ActiveNames(IStreamingDevice device) =>
        device.DataChannels.Where(c => c.IsActive).Select(c => c.Name).Order().ToArray();

    private static string[] SubscribedNames(IStreamingDevice device) =>
        LoggingManager.Instance.SubscribedChannels
            .Where(c => device.DataChannels.Any(d => ReferenceEquals(d, c)))
            .Select(c => c.Name).Order().ToArray();

    /// <summary>
    /// The bench report: set up channels and a rate, capture them, change the device, activate the
    /// profile. The device must come back to exactly what was captured: the rate, the channels,
    /// and nothing that was switched on after the capture, in the app and on the board alike.
    /// </summary>
    [Fact]
    public async Task Activating_a_captured_profile_puts_the_device_back_to_exactly_its_rate_and_channels()
    {
        var device = Connect("PROFILE-RT-1");
        Enable(device, "AI0", "AI1");
        device.StreamingFrequency = 500;
        var profile = CaptureCurrentSettings("Bench round trip");

        Disable(device, "AI0", "AI1");
        Enable(device, "AI2");
        device.StreamingFrequency = 200;

        await Activate(profile);

        Assert.True(profile.IsProfileActive);
        Assert.Equal(500, device.StreamingFrequency);
        Assert.Equal(["AI0", "AI1"], ActiveNames(device));
        Assert.Equal(["AI0", "AI1"], SubscribedNames(device));
        Assert.Equal("3", device.LastAdcMask()); // AI0 | AI1: what the board ends on agrees with the app
        Assert.Equal(string.Empty, _pane.DrawerError);
        Assert.False(_pane.HasNotice);
    }

    /// <summary>
    /// A channel switched on while a profile is active is not the profile's, but turning the
    /// profile off (or switching away from it) still switches it off. Otherwise it stays
    /// subscribed with the board no longer sending it (#529 audit).
    /// </summary>
    [Fact]
    public async Task Deactivating_a_profile_also_drops_channels_switched_on_outside_it()
    {
        var device = Connect("PROFILE-OFF-1");
        Enable(device, "AI0", "AI1");
        var profile = CaptureCurrentSettings("Off drops extras");
        await Activate(profile);

        Enable(device, "AI2");
        await Activate(profile); // the same tile again: toggle off

        Assert.False(profile.IsProfileActive);
        Assert.Empty(ActiveNames(device));
        Assert.Empty(SubscribedNames(device));
    }

    /// <summary>
    /// #670 through a profile: two channels the user gave one name, both switched on and captured.
    /// Activating the profile must switch on both inputs, not the first of them twice.
    /// </summary>
    [Fact]
    public async Task A_profile_with_two_channels_of_one_name_switches_on_both_inputs()
    {
        var device = Connect("PROFILE-DUP-1");
        var temps = device.DataChannels.Skip(2).ToList();   // AI2 and AI3
        foreach (var channel in temps)
        {
            channel.Name = "Temp";
            device.AddChannel(channel);
            LoggingManager.Instance.Subscribe(channel);
        }
        var profile = CaptureCurrentSettings("Two of one name");
        foreach (var channel in temps)
        {
            LoggingManager.Instance.Unsubscribe(channel);
            device.RemoveChannel(channel);
        }

        await Activate(profile);

        Assert.True(profile.IsProfileActive);
        Assert.Equal("12", device.LastAdcMask()); // AI2 | AI3
        Assert.All(temps, channel => Assert.True(channel.IsActive));
        Assert.Equal(2, LoggingManager.Instance.SubscribedChannels.Count(c => temps.Any(t => ReferenceEquals(t, c))));
    }

    /// <summary>
    /// PR #678 review: AI0 and AI1 both named Temp, only AI1 on when the profile is captured. The
    /// profile must turn on AI1, the input it captured, and not the first channel with that name.
    /// </summary>
    [Fact]
    public async Task A_profile_with_one_of_two_same_named_channels_turns_on_the_input_it_captured()
    {
        var device = Connect("PROFILE-DUP-2");
        device.DataChannels[0].Name = "Temp";
        device.DataChannels[1].Name = "Temp";
        device.AddChannel(device.DataChannels[1]);
        LoggingManager.Instance.Subscribe(device.DataChannels[1]);
        var profile = CaptureCurrentSettings("One of two of one name");
        LoggingManager.Instance.Unsubscribe(device.DataChannels[1]);
        device.RemoveChannel(device.DataChannels[1]);

        await Activate(profile);

        Assert.True(profile.IsProfileActive);
        Assert.Equal("2", device.LastAdcMask()); // AI1, as captured
        Assert.False(device.DataChannels[0].IsActive);
        Assert.True(device.DataChannels[1].IsActive);
    }

    /// <summary>The same through the profiles file: the input survives the save and the load.</summary>
    [Fact]
    public async Task A_profile_read_back_from_the_file_turns_on_the_input_it_captured()
    {
        var device = Connect("PROFILE-DUP-3");
        device.DataChannels[0].Name = "Temp";
        device.DataChannels[1].Name = "Temp";
        device.AddChannel(device.DataChannels[1]);
        LoggingManager.Instance.Subscribe(device.DataChannels[1]);
        var saved = CaptureCurrentSettings("One of two of one name, from the file");
        LoggingManager.Instance.Unsubscribe(device.DataChannels[1]);
        device.RemoveChannel(device.DataChannels[1]);

        LoggingManager.Instance.LoadProfilesFromXml();
        var reloaded = LoggingManager.Instance.SubscribedProfiles.Single(p => p.ProfileId == saved.ProfileId);
        Assert.NotSame(saved, reloaded);
        await Activate(reloaded);

        Assert.Equal("2", device.LastAdcMask());
        Assert.True(device.DataChannels[1].IsActive);
        Assert.False(device.DataChannels[0].IsActive);
    }

    /// <summary>
    /// A profile saved before input numbers were stored names a channel only by its name. When two
    /// channels now have that name, the app cannot tell which input the profile means. It must say
    /// so and change nothing, not turn on the first of them.
    /// </summary>
    [Fact]
    public async Task An_older_profile_that_names_one_of_two_same_named_channels_is_refused_with_the_reason()
    {
        var device = Connect("PROFILE-OLD-1");
        device.DataChannels[0].Name = "Temp";
        device.DataChannels[1].Name = "Temp";
        var profile = OlderProfile("Older, ambiguous", device, "Temp");

        await Activate(profile);

        Assert.False(profile.IsProfileActive);
        Assert.Contains("Temp", _pane.DrawerError, StringComparison.Ordinal);
        Assert.DoesNotContain(device.DataChannels, c => c.IsActive);
    }

    /// <summary>
    /// The new-profile drawer is the other way to make a profile. With two channels named Temp and
    /// only the second chosen, the profile must turn on the second.
    /// </summary>
    [Fact]
    public async Task A_new_profile_with_one_of_two_same_named_channels_turns_on_the_input_chosen()
    {
        var device = Connect("PROFILE-DUP-NEW");
        device.DataChannels[0].Name = "Temp";
        device.DataChannels[1].Name = "Temp";
        var profile = SaveNewProfile("New, one of two of one name", device, device.DataChannels[1]);

        await Activate(profile);

        Assert.Equal("2", device.LastAdcMask());
        Assert.False(device.DataChannels[0].IsActive);
        Assert.True(device.DataChannels[1].IsActive);
    }

    /// <summary>
    /// The new-profile drawer: choose one connected device and some of its channels, and save.
    /// Every other device stays unchosen.
    /// </summary>
    private Profile SaveNewProfile(string name, IStreamingDevice device, params IChannel[] channels)
    {
        _pane.OpenNewDrawerCommand.Execute(null);
        var item = _pane.NewDeviceItems.Single(d => ReferenceEquals(d.Device, device));
        item.IsSelected = true;
        foreach (var channelItem in item.ChannelItems)
        {
            channelItem.IsSelected = channels.Any(c => ReferenceEquals(c, channelItem.Channel));
        }
        _pane.NewProfileName = name;
        var before = LoggingManager.Instance.SubscribedProfiles.ToList();
        _pane.SaveNewProfileCommand.Execute(null);
        return LoggingManager.Instance.SubscribedProfiles.Except(before).Single();
    }

    /// <summary>
    /// PR #678 review, round 2: two boards of one model, AI1 named differently on each. The
    /// new-profile drawer saves only the second board's AI1, and the profile is read back from the
    /// file. Activation must turn on AI1 of the board the profile was saved from, and leave the
    /// first board as it was. Boards with no serial number are told apart by the MAC address the
    /// profile stores. Without that, activation fell back to the first board of the model, where
    /// the input number alone found an AI1.
    /// </summary>
    [Theory]
    [InlineData("", "AA-BB-CC-00-00-01", "", "AA-BB-CC-00-00-02")]  // no serial: the reviewer's case
    [InlineData("PROFILE-TWO-1", "", "PROFILE-TWO-2", "")]          // serial numbers
    public async Task A_new_profile_from_the_second_of_two_boards_turns_on_that_board(
        string firstSerial, string firstMac, string secondSerial, string secondMac)
    {
        var first = Connect(firstSerial, macAddress: firstMac, name: "Nq1 left");
        var second = Connect(secondSerial, macAddress: secondMac, name: "Nq1 right");
        first.DataChannels[1].Name = "Left";
        second.DataChannels[1].Name = "Right";
        var saved = SaveNewProfile("Second board, AI1", second, second.DataChannels[1]);

        LoggingManager.Instance.LoadProfilesFromXml();
        var profile = LoggingManager.Instance.SubscribedProfiles.Single(p => p.ProfileId == saved.ProfileId);
        Assert.NotSame(saved, profile);
        Assert.Equal(secondMac, profile.Devices.Single().MacAddress);
        await Activate(profile);

        Assert.DoesNotContain(first.DataChannels, c => c.IsActive);
        Assert.DoesNotContain(first.Sent, s => s.StartsWith("ENAble:VOLTage:DC ", StringComparison.Ordinal));
        Assert.True(profile.IsProfileActive);
        Assert.Equal("2", second.LastAdcMask()); // AI1 of the board the profile was saved from
        Assert.True(second.DataChannels[1].IsActive);
    }

    /// <summary>
    /// The board a profile was saved from is not connected, and another board of its model is. The
    /// profile must not guess that board: activation is refused with the reason, which names the
    /// board, and the other board stays as it was. Once for a board found by serial number, once
    /// for a board with no serial, found by MAC address.
    /// </summary>
    [Theory]
    [InlineData("PROFILE-AWAY-1", "", "PROFILE-AWAY-2", "", "PROFILE-AWAY-1")]
    [InlineData("", "AA-BB-CC-00-00-11", "", "AA-BB-CC-00-00-12", "AA-BB-CC-00-00-11")]
    public async Task A_profile_whose_board_is_not_connected_does_not_change_another_board(
        string savedSerial, string savedMac, string otherSerial, string otherMac, string named)
    {
        var saved = Connect(savedSerial, macAddress: savedMac, name: "Nq1 saved");
        var other = Connect(otherSerial, macAddress: otherMac, name: "Nq1 other");
        var profile = SaveNewProfile("Board away", saved, saved.DataChannels[1]);
        Disconnect(saved);
        Enable(other, "AI3");

        await Activate(profile);

        Assert.Equal(["AI3"], ActiveNames(other));
        Assert.Equal(["AI3"], SubscribedNames(other));
        Assert.False(profile.IsProfileActive);
        Assert.Contains("is not connected", _pane.DrawerError, StringComparison.Ordinal);
        Assert.Contains(named, _pane.DrawerError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A board that reports no serial number and no MAC address leaves a profile nothing to find it
    /// by. With one such board connected, it is the board: a reconnect (a new device object) still
    /// gets the profile. With two of them, the profile cannot say which, so it is refused and
    /// neither board changes.
    /// </summary>
    [Fact]
    public async Task A_profile_from_a_board_with_no_identifier_applies_to_the_only_board_and_refuses_two()
    {
        var board = Connect(string.Empty, name: "Nq1 bare");
        var profile = SaveNewProfile("Bare board", board, board.DataChannels[1]);
        Disconnect(board);
        var again = Connect(string.Empty, name: "Nq1 bare");

        await Activate(profile);

        Assert.True(profile.IsProfileActive);
        Assert.Equal("2", again.LastAdcMask());
        await Activate(profile); // toggle off

        var another = Connect(string.Empty, name: "Nq1 bare");
        Enable(again, "AI0");
        Enable(another, "AI3");
        await Activate(profile);

        Assert.Equal(["AI0"], ActiveNames(again));
        Assert.Equal(["AI3"], ActiveNames(another));
        Assert.False(profile.IsProfileActive);
        Assert.Contains("more than one device", _pane.DrawerError, StringComparison.Ordinal);
    }

    /// <summary>
    /// An older profile (no input numbers) from a board with no serial also looks for its board by
    /// MAC address first, so its channel names are checked on the right board.
    /// </summary>
    [Fact]
    public async Task An_older_profile_from_a_board_with_no_serial_finds_its_board_by_MAC_address()
    {
        var first = Connect(string.Empty, macAddress: "AA-BB-CC-00-00-21", name: "Nq1 left");
        var second = Connect(string.Empty, macAddress: "AA-BB-CC-00-00-22", name: "Nq1 right");
        var profile = OlderProfile("Older, second board", second, "AI1");

        await Activate(profile);

        Assert.DoesNotContain(first.DataChannels, c => c.IsActive);
        Assert.True(profile.IsProfileActive);
        Assert.Equal("2", second.LastAdcMask());
    }

    /// <summary>
    /// An older profile with as many entries of one name as the device has channels of that name is
    /// not ambiguous: each entry claims one, and both inputs are turned on (#670).
    /// </summary>
    [Fact]
    public async Task An_older_profile_with_two_entries_for_two_same_named_channels_turns_on_both()
    {
        var device = Connect("PROFILE-OLD-3");
        device.DataChannels[2].Name = "Temp";
        device.DataChannels[3].Name = "Temp";
        var profile = OlderProfile("Older, two of one name", device, "Temp", "Temp");

        await Activate(profile);

        Assert.True(profile.IsProfileActive);
        Assert.Equal("12", device.LastAdcMask()); // AI2 | AI3
    }

    /// <summary>An older profile whose names are unique on the device still applies by name.</summary>
    [Fact]
    public async Task An_older_profile_with_unique_names_still_applies_by_name()
    {
        var device = Connect("PROFILE-OLD-2");
        var profile = OlderProfile("Older, unique", device, "AI2");

        await Activate(profile);

        Assert.True(profile.IsProfileActive);
        Assert.Equal("4", device.LastAdcMask()); // AI2
    }

    /// <summary>
    /// A profile in the shape files had before input numbers were stored: each channel entry holds
    /// only a name and a type.
    /// </summary>
    private static Profile OlderProfile(string name, IStreamingDevice device, params string[] channelNames)
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
                    DeviceName = device.Name,
                    DevicePartName = device.DevicePartNumber,
                    DeviceSerialNo = device.DeviceSerialNo,
                    MacAddress = device.MacAddress,
                    SamplingFrequency = 100,
                    Channels = [.. channelNames.Select(n => new ProfileChannel
                    {
                        Name = n,
                        Type = "Analog Input",
                        IsChannelActive = true,
                        SerialNo = device.DeviceSerialNo,
                    })],
                },
            ],
        };
        Assert.True(LoggingManager.Instance.SubscribeProfile(profile));
        return profile;
    }

    /// <summary>
    /// The same round trip through the file: the profile activated is the one read back from the
    /// profiles XML, which is what a profile saved in an earlier session is. Passes on the unfixed
    /// code too; it pins that the save and the load agree, which the issue asked to be checked.
    /// </summary>
    [Fact]
    public async Task A_profile_read_back_from_the_file_applies_the_rate_and_channels_it_was_saved_with()
    {
        var device = Connect("PROFILE-RT-2");
        Enable(device, "AI1", "AI3");
        device.StreamingFrequency = 250;
        var saved = CaptureCurrentSettings("Bench file round trip");

        Disable(device, "AI1", "AI3");
        device.StreamingFrequency = 1000;

        LoggingManager.Instance.LoadProfilesFromXml();
        var reloaded = LoggingManager.Instance.SubscribedProfiles.Single(p => p.ProfileId == saved.ProfileId);
        Assert.NotSame(saved, reloaded);

        await Activate(reloaded);

        Assert.True(reloaded.IsProfileActive);
        Assert.Equal(250, device.StreamingFrequency);
        Assert.Equal(["AI1", "AI3"], ActiveNames(device));
        Assert.Equal("10", device.LastAdcMask()); // AI1 | AI3
    }

    /// <summary>
    /// A click while logging is on still changes nothing, but now says why. It used to be a
    /// breadcrumb and nothing else.
    /// </summary>
    [Fact]
    public async Task Activating_while_logging_says_why_and_changes_nothing()
    {
        var device = Connect("PROFILE-LOG-1");
        Enable(device, "AI0");
        device.StreamingFrequency = 500;
        var profile = CaptureCurrentSettings("Bench while logging");
        Disable(device, "AI0");
        Enable(device, "AI2");
        device.StreamingFrequency = 200;

        LoggingManager.Instance.Active = true;
        await Activate(profile);

        Assert.False(profile.IsProfileActive);
        Assert.Equal(200, device.StreamingFrequency);
        Assert.Equal(["AI2"], ActiveNames(device));
        Assert.Equal(ProfilesPaneViewModel.LoggingActiveNotice, _pane.Notice);
        Assert.True(_pane.HasNotice);

        LoggingManager.Instance.Active = false;
        Assert.False(_pane.HasNotice);
    }

    /// <summary>
    /// A device that matches the profile by model but has none of its channels used to be cleared
    /// of nothing, given nothing, and shown under an ACTIVE tile. Now the profile is refused with
    /// the reason, and the device is left as it was.
    /// </summary>
    [Fact]
    public async Task A_profile_whose_channels_match_nothing_is_refused_with_the_reason()
    {
        var original = Connect("PROFILE-MISS-1");
        var profile = OlderProfile("Bench other model", original, "AI3");
        Disconnect(original);

        // Same part number, so the model fallback matches it; it has no channel AI3. The profile is
        // in the older shape (names only): since PR #678 a captured profile records its board and
        // its inputs, and does not fall back to another board of the model at all.
        var other = Connect("PROFILE-MISS-2", channelCount: 2);
        Enable(other, "AI1");

        await Activate(profile);

        Assert.False(profile.IsProfileActive);
        Assert.Equal(["AI1"], ActiveNames(other));
        Assert.Equal(["AI1"], SubscribedNames(other));
        Assert.Equal(ProfilesPaneViewModel.NoChannelsMatchedMessage, _pane.DrawerError);
    }

    /// <summary>
    /// Two matched devices, one of which has none of its saved channels: that device would be
    /// cleared and given nothing under an ACTIVE tile. The whole activation is refused, and
    /// neither device is touched (#529 audit).
    /// </summary>
    [Fact]
    public async Task A_matched_device_with_none_of_its_channels_refuses_the_whole_profile()
    {
        var x = Connect("PROFILE-PER-X");
        var y = Connect("PROFILE-PER-Y");
        Enable(x, "AI0");
        Enable(y, "AI1");
        var profile = CaptureCurrentSettings("Per device");
        Disable(x, "AI0");
        Disable(y, "AI1");
        Disconnect(y);

        // Same serial, so it matches Y's entry, but it has no input 1. (This used to give it other
        // channel names; see the note in the test above.)
        var renamed = Connect("PROFILE-PER-Y", channelCount: 1);
        Enable(renamed, "AI0");
        Enable(x, "AI3");

        await Activate(profile);

        Assert.False(profile.IsProfileActive);
        Assert.Equal(["AI3"], ActiveNames(x));
        Assert.Equal(["AI0"], ActiveNames(renamed));
        Assert.Equal(ProfilesPaneViewModel.NoChannelsMatchedMessage, _pane.DrawerError);
    }

    /// <summary>
    /// #530: a device saved with every channel off is restored to all-off, even while the
    /// profile's other device is disconnected.
    /// </summary>
    [Fact]
    public async Task A_device_saved_all_off_is_restored_while_the_other_device_is_away()
    {
        var x = Connect("PROFILE-OFF-X");
        var y = Connect("PROFILE-OFF-Y");
        Enable(x, "AI0");
        var profile = CaptureCurrentSettings("One device off");
        Disable(x, "AI0");
        Disconnect(x);
        Enable(y, "AI2");

        await Activate(profile);

        Assert.True(profile.IsProfileActive);
        Assert.Empty(ActiveNames(y));
        Assert.Empty(SubscribedNames(y));
        Assert.Equal(string.Empty, _pane.DrawerError);
    }

    /// <summary>
    /// The SWITCH confirm is in-pane only, so logging can start from another tab while it is
    /// open. Confirming then must still refuse: the active profile and its channels stay as they
    /// were (#529 audit).
    /// </summary>
    [Fact]
    public async Task Logging_started_while_the_switch_confirm_is_open_still_refuses()
    {
        var device = Connect("PROFILE-SW-1");
        Enable(device, "AI0");
        var first = CaptureCurrentSettings("First");
        Disable(device, "AI0");
        Enable(device, "AI1");
        var second = CaptureCurrentSettings("Second");
        Disable(device, "AI1");
        await Activate(first);

        var switching = Activate(second);
        Assert.True(_pane.ConfirmOverlay.IsOpen);
        LoggingManager.Instance.Active = true;
        _pane.ConfirmOverlay.AffirmativeCommand.Execute(null);
        await switching;

        Assert.True(first.IsProfileActive);
        Assert.False(second.IsProfileActive);
        Assert.Equal(["AI0"], ActiveNames(device));
        Assert.Equal(["AI0"], SubscribedNames(device));
        Assert.Equal(ProfilesPaneViewModel.LoggingActiveNotice, _pane.Notice);
    }
}
