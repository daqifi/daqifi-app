using Daqifi.Desktop.Channel;
using Daqifi.Desktop.DialogService;
using Daqifi.Desktop.ViewModels;
using Xunit;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #540: when a channel's scale expression has no valid result for a sample, the app drops
/// the sample and says so once in the notification bell. These pin the desktop list's half: the
/// notice is added, it is not stacked when the same fault recurs in a later session, and it is
/// device-owned so the per-device pruning removes it when the device goes.
/// </summary>
/// <remarks>
/// <c>OnScaleResultsRejected</c> itself only posts to the UI thread, which this test host cannot
/// pump, so the tests call the method it posts.
/// </remarks>
[Collection(AppHostCollection.Name)]
public class ScaleResultsRejectedNoticeTests
{
    private sealed class NamedChannel : AbstractChannel
    {
        public NamedChannel(string name, string? serial)
        {
            Name = name;
            DeviceSerialNo = serial!;
        }

        public override string Name { get; set; }

        public override ChannelDirection Direction { get; set; } = ChannelDirection.Input;

        public override int Index => 0;

        public override ChannelType Type => ChannelType.Analog;

        public override bool IsActive { get; set; }

        public override bool IsDigital => false;

        public override bool IsAnalog => true;
    }

    private static DaqifiViewModel NewViewModel()
    {
        Daqifi.Desktop.App.InitializeMobile();
        return new DaqifiViewModel(new NullDialogService());
    }

    [Fact]
    public void The_notice_is_added_once_however_often_the_fault_recurs()
    {
        var viewModel = NewViewModel();
        var channel = new NamedChannel("AI3", "7E2815916200E898");

        viewModel.AddScaleResultsRejectedNotice(channel);
        viewModel.AddScaleResultsRejectedNotice(channel);

        var notice = Assert.Single(viewModel.NotificationList);
        Assert.Equal(channel.RejectedScaleResultsNotification, notice.Message);
        Assert.Equal(1, viewModel.NotificationCount);
    }

    [Fact]
    public void Each_channel_gets_its_own_notice()
    {
        var viewModel = NewViewModel();

        viewModel.AddScaleResultsRejectedNotice(new NamedChannel("AI0", "SN-1"));
        viewModel.AddScaleResultsRejectedNotice(new NamedChannel("AI1", "SN-1"));

        Assert.Equal(2, viewModel.NotificationList.Count);
    }

    [Fact]
    public void The_notice_belongs_to_the_device_so_it_goes_when_the_device_goes()
    {
        var viewModel = NewViewModel();

        viewModel.AddScaleResultsRejectedNotice(new NamedChannel("AI0", "SN-GONE"));
        Assert.Equal("SN-GONE", Assert.Single(viewModel.NotificationList).DeviceSerialNo);

        // No device with that serial is connected, which is the state after it is unplugged.
        viewModel.RemoveNotification();

        Assert.Empty(viewModel.NotificationList);
    }

    [Fact]
    public void A_channel_with_no_serial_is_not_pruned_on_the_next_pass()
    {
        // An empty serial matches no connected device, so storing it as-is would have the notice
        // removed by the very next UpdateUi pass, before anyone could read it.
        var viewModel = NewViewModel();

        viewModel.AddScaleResultsRejectedNotice(new NamedChannel("AI0", string.Empty));
        viewModel.RemoveNotification();

        Assert.Null(Assert.Single(viewModel.NotificationList).DeviceSerialNo);
    }
}
