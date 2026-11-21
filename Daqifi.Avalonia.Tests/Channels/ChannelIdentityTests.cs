using Daqifi.Core.Communication.Messages;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Logger;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;
using CoreAnalogChannel = Daqifi.Core.Channel.AnalogChannel;

namespace Daqifi.Avalonia.Tests.Channels;

/// <summary>
/// #670: the app used a channel's name as its identity, and the user can edit that name freely. With
/// two channels of one device under one name, a click on the second tile turned on the FIRST input on
/// the board, the second never streamed, and the session recorded nothing.
/// </summary>
/// <remarks>
/// <para>
/// A channel is now identified by the input it stands for: its device's serial number, its type and
/// its index. These rows use real <see cref="AnalogChannel"/>s on a device whose
/// <c>SendMessage</c> is recorded, so the enable mask asserted here is the one the board would get.
/// </para>
/// <para>
/// Identity assertions use <see cref="Assert.Same"/> or <c>IsActive</c> on each instance, never
/// <see cref="Assert.Equal{T}(T, T)"/> on channels: that would compare through the very equality
/// under test.
/// </para>
/// </remarks>
public sealed class ChannelIdentityTests
{
    private const string Serial = "SN-670";

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

    /// <summary>Nothing on the subscribe path may touch the database.</summary>
    private sealed class UnusedContextFactory : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() =>
            throw new InvalidOperationException("Channel subscription must not touch the database.");
    }

    /// <summary>A device whose analog input <c>i</c> carries <c>names[i]</c>.</summary>
    private static BenchDevice Device(string serial, params string[] names)
    {
        var device = new BenchDevice { Name = "Nq1", DeviceSerialNo = serial };
        for (var i = 0; i < names.Length; i++)
        {
            device.DataChannels.Add(new AnalogChannel(device, new CoreAnalogChannel(i)) { Name = names[i] });
        }

        return device;
    }

    private static LoggingManager NewManager() => new(new UnusedContextFactory());

    /// <summary>
    /// The board half of the issue's reproduction: AI1 renamed to "AI0", then only its tile clicked.
    /// The board must be told to stream input 1, and only that tile may read active.
    /// </summary>
    [Fact]
    public void Turning_on_the_second_of_two_same_named_channels_enables_the_second_input()
    {
        var device = Device(Serial, "AI0", "AI0");
        var first = device.DataChannels[0];
        var second = device.DataChannels[1];

        device.AddChannel(second);

        Assert.Equal("2", device.LastAdcMask());
        Assert.True(second.IsActive, "The clicked channel must read active.");
        Assert.False(first.IsActive, "The other channel of the same name was not clicked.");
    }

    /// <summary>
    /// The second click of the issue: turning the renamed tile off must clear that tile, and leave the
    /// other one, which the user did not touch, active and on the board.
    /// </summary>
    [Fact]
    public void Turning_off_one_of_two_same_named_channels_turns_off_that_one_only()
    {
        var device = Device(Serial, "AI0", "AI0");
        var first = device.DataChannels[0];
        var second = device.DataChannels[1];
        device.AddChannel(first);
        device.AddChannel(second);
        Assert.Equal("3", device.LastAdcMask());

        device.RemoveChannel(second);

        Assert.Equal("1", device.LastAdcMask());
        Assert.True(first.IsActive, "The channel the user did not turn off must stay active.");
        Assert.False(second.IsActive, "The channel the user turned off must read inactive.");
    }

    /// <summary>A profile that switches on two same-named channels switches on both inputs.</summary>
    [Fact]
    public void Switching_on_two_same_named_channels_together_enables_both_inputs()
    {
        var device = Device(Serial, "Temp", "Temp", "AI2");
        var first = device.DataChannels[0];
        var second = device.DataChannels[1];

        device.AddChannels([first, second]);

        Assert.Equal("3", device.LastAdcMask());
        Assert.True(first.IsActive);
        Assert.True(second.IsActive);
        Assert.False(device.DataChannels[2].IsActive);
    }

    /// <summary>
    /// The logging half: <see cref="LoggingManager.Subscribe"/> skipped a channel whose serial number
    /// and name were already subscribed, so the second of two same-named channels was never wired and
    /// nothing it sent was recorded.
    /// </summary>
    [Fact]
    public void Two_same_named_channels_of_one_device_are_both_subscribed()
    {
        var device = Device(Serial, "AI0", "AI0");
        var manager = NewManager();

        manager.Subscribe(device.DataChannels[0]);
        manager.Subscribe(device.DataChannels[1]);

        Assert.Equal(2, manager.SubscribedChannels.Count);
        Assert.Contains(manager.SubscribedChannels, c => ReferenceEquals(c, device.DataChannels[0]));
        Assert.Contains(manager.SubscribedChannels, c => ReferenceEquals(c, device.DataChannels[1]));
    }

    /// <summary>Subscribing the same channel twice still subscribes it once.</summary>
    [Fact]
    public void Subscribing_a_channel_again_after_a_rename_keeps_one_subscription()
    {
        var device = Device(Serial, "AI0");
        var channel = device.DataChannels[0];
        var manager = NewManager();

        manager.Subscribe(channel);
        channel.Name = "Temp";
        manager.Subscribe(channel);

        Assert.Same(channel, Assert.Single(manager.SubscribedChannels));
    }

    /// <summary>
    /// #597's reconnect path. The old connection's channel instance had been renamed; the new
    /// connection's instance of the same input carries the default name again. The leftover must still
    /// be found and replaced, or the phone records into a channel that no longer exists.
    /// </summary>
    [Fact]
    public void A_renamed_leftover_from_an_earlier_connection_is_replaced_by_the_same_input()
    {
        var manager = NewManager();
        var oldInstance = Device(Serial, "Temp").DataChannels[0];
        manager.Subscribe(oldInstance);
        oldInstance.IsActive = false;   // the old connection is gone

        var newInstance = Device(Serial, "AI0").DataChannels[0];
        manager.SubscribeReplacingStale(newInstance);

        Assert.Same(newInstance, Assert.Single(manager.SubscribedChannels));
    }

    /// <summary>
    /// The identity itself. Two inputs of one device are different channels whatever the user calls
    /// them; two objects for the same input of the same device are the same channel; the same input
    /// number on another device is not.
    /// </summary>
    [Fact]
    public void Channel_identity_is_the_device_the_type_and_the_index_and_not_the_name()
    {
        var device = Device(Serial, "AI0", "AI0");
        var sameInputAgain = Device(Serial, "Temp").DataChannels[0];
        var otherDevice = Device("SN-OTHER", "AI0").DataChannels[0];

        Assert.False(device.DataChannels[0].Equals(device.DataChannels[1]),
            "Two inputs that share a name are still two channels.");
        Assert.True(device.DataChannels[0].Equals(sameInputAgain),
            "Two objects for one input of one device are one channel, whatever their names.");
        Assert.False(device.DataChannels[0].Equals(otherDevice),
            "Input 0 of another device is another channel.");
    }

    /// <summary>
    /// The hash code followed the name, so a rename moved a channel to another bucket and every hashed
    /// collection holding it lost it.
    /// </summary>
    [Fact]
    public void A_renamed_channel_is_still_found_in_a_hashed_collection()
    {
        var channel = Device(Serial, "AI0").DataChannels[0];
        var set = new HashSet<IChannel> { channel };

        channel.Name = "Temp";

        Assert.Contains(channel, set);
    }
}
