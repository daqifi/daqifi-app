using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// #597: the phone records a stream through <see cref="LoggingManager"/>, and it reconnects to the
/// same board with NEW channel instances. <see cref="LoggingManager.Subscribe"/> treats a channel as
/// subscribed when any entry has its serial number and name, so the old connection's instance made
/// it skip the new one, and a recording after a reconnect kept no samples.
/// <see cref="LoggingManager.SubscribeReplacingStale"/> replaces such leftovers.
/// </summary>
/// <remarks>
/// Every identity assertion uses <see cref="Assert.Same"/>: channel equality is name-only (see
/// <c>LoggingManagerSubscribedChannelsTests</c>), so <c>Assert.Equal</c> would pass on either
/// instance.
/// </remarks>
public class LoggingManagerSubscribeReplacingStaleTests
{
    private const string Board = "SERIAL-A";
    private const string OtherBoard = "SERIAL-B";

    private sealed class TestChannel(string name, string deviceSerialNo) : AbstractChannel
    {
        private string _name = name;

        public TestChannel Init()
        {
            DeviceSerialNo = deviceSerialNo;
            return this;
        }

        public override string Name
        {
            get => _name;
            set => _name = value;
        }

        public override ChannelDirection Direction { get; set; } = ChannelDirection.Input;
        public override int Index => 0;
        public override ChannelType Type => ChannelType.Analog;
        public override bool IsActive { get; set; }
        public override bool IsDigital => false;
        public override bool IsAnalog => true;
    }

    /// <summary>Nothing on the subscribe path may touch the database.</summary>
    private sealed class UnusedContextFactory : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() =>
            throw new InvalidOperationException("Channel subscription must not touch the database.");
    }

    private static LoggingManager NewManager() => new(new UnusedContextFactory());

    private static TestChannel Channel(string name, string serial) => new TestChannel(name, serial).Init();

    [Fact]
    public void Plain_Subscribe_skips_a_new_instance_of_a_channel_an_old_connection_left_behind()
    {
        // The premise: without the replacing method, the reconnect's channel is never subscribed.
        var manager = NewManager();
        var oldInstance = Channel("AI0", Board);
        manager.Subscribe(oldInstance);
        oldInstance.IsActive = false;   // the old device is gone

        var newInstance = Channel("AI0", Board);
        manager.Subscribe(newInstance);

        Assert.Same(oldInstance, Assert.Single(manager.SubscribedChannels));
    }

    [Fact]
    public void A_leftover_instance_is_replaced_by_the_new_one()
    {
        var manager = NewManager();
        var oldInstance = Channel("AI0", Board);
        manager.Subscribe(oldInstance);
        oldInstance.IsActive = false;

        var newInstance = Channel("AI0", Board);
        manager.SubscribeReplacingStale(newInstance);

        Assert.Same(newInstance, Assert.Single(manager.SubscribedChannels));
        Assert.True(newInstance.IsActive);
    }

    [Fact]
    public void An_already_subscribed_instance_stays_subscribed_once()
    {
        var manager = NewManager();
        var channel = Channel("AI0", Board);
        manager.Subscribe(channel);

        manager.SubscribeReplacingStale(channel);

        Assert.Same(channel, Assert.Single(manager.SubscribedChannels));
    }

    [Fact]
    public void The_same_channel_of_another_board_is_kept()
    {
        var manager = NewManager();
        var otherBoard = Channel("AI0", OtherBoard);
        manager.Subscribe(otherBoard);

        var channel = Channel("AI0", Board);
        manager.SubscribeReplacingStale(channel);

        Assert.Equal(2, manager.SubscribedChannels.Count);
        Assert.Contains(manager.SubscribedChannels, c => ReferenceEquals(c, otherBoard));
        Assert.Contains(manager.SubscribedChannels, c => ReferenceEquals(c, channel));
    }
}
