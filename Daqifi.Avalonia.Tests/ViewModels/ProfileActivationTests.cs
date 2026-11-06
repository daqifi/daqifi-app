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

    private BenchDevice Connect(string serial, string channelPrefix = "AI", int channelCount = 4)
    {
        var device = new BenchDevice { Name = serial, DeviceSerialNo = serial };
        device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };
        device.Metadata.PartNumber = "Nq1";
        for (var i = 0; i < channelCount; i++)
        {
            device.DataChannels.Add(new AnalogChannel(device, new CoreAnalogChannel(i)) { Name = $"{channelPrefix}{i}" });
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
        Enable(original, "AI0");
        var profile = CaptureCurrentSettings("Bench other model");
        Disable(original, "AI0");
        Disconnect(original);

        // Same part number, so the model fallback matches it; different channel names.
        var other = Connect("PROFILE-MISS-2", channelPrefix: "CH");
        Enable(other, "CH1");

        await Activate(profile);

        Assert.False(profile.IsProfileActive);
        Assert.Equal(["CH1"], ActiveNames(other));
        Assert.Equal(["CH1"], SubscribedNames(other));
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

        // Same serial, so it matches Y's entry, but its channels are named differently.
        var renamed = Connect("PROFILE-PER-Y", channelPrefix: "CH");
        Enable(renamed, "CH2");
        Enable(x, "AI3");

        await Activate(profile);

        Assert.False(profile.IsProfileActive);
        Assert.Equal(["AI3"], ActiveNames(x));
        Assert.Equal(["CH2"], ActiveNames(renamed));
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
