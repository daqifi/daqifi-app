using Daqifi.Avalonia.Tests.Device;
using Daqifi.Avalonia.Views;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Channel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;
using CoreStreamingDevice = Daqifi.Core.Device.DaqifiStreamingDevice;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// #668 and #670 on the phone. The shell's channel selector, its streaming set and its live-plot
/// series were keyed by channel name: the selector kept the names the channels had at connect time,
/// and the series map looked each channel up by its CURRENT name. A channel renamed in the Channels
/// pane was therefore never streamed, and a channel renamed while streaming stopped drawing.
/// </summary>
/// <remarks>
/// Below the view-model everything is real, as in <c>MobileShellSilentStreamWatchdogTests</c>: the
/// app's device wrapper over a real Core device on a silent transport, with channels populated by
/// Core's own status handling. In the <c>ConnectionManager</c> singleton collection because the
/// shell's adopt step registers the device there.
/// </remarks>
[Collection(ConnectionManagerSingletonCollection.Name)]
public sealed class MobileShellChannelIdentityTests : IDisposable
{
    private const int AnalogPorts = 4;

    private readonly CapturingTransport _transport = new();
    private readonly CoreStreamingDevice _core;
    private readonly ShellTestDevice _device;
    private readonly MobileShellViewModel _shell = new();
    private long _ticks;

    public MobileShellChannelIdentityTests()
    {
        _shell.PostToUiThread = action => action();
        _core = new CoreStreamingDevice("core", _transport, NullLogger.Instance);
        _core.Connect();
        _core.PopulateChannelsFromStatus(new DaqifiOutMessage { AnalogInPortNum = AnalogPorts, AnalogInRes = 65535 });

        _device = new ShellTestDevice("Channel identity test device");
        _device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };
        _device.AttachCore(_core);
        _device.SyncFromCore(_core);

        _device.AdoptInto(_shell);
        Assert.Equal(AnalogPorts, _shell.Channels.Count);
    }

    public void Dispose()
    {
        _shell.Dispose();
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();
    }

    /// <summary>
    /// A channel renamed after the phone connected, then picked alone in the selector, must be the
    /// channel that streams.
    /// </summary>
    [Fact]
    public void A_channel_renamed_after_connecting_streams_when_it_is_selected()
    {
        Analog(3).Name = "Temp";
        SelectOnly(3);

        _shell.StreamToggleCommand.Execute(null);

        Assert.True(_shell.IsStreaming, $"Streaming did not start ({_shell.Status}).");
        Assert.Equal([3], ActiveAnalogIndexes());
        Assert.Single(_shell.Series);
    }

    /// <summary>
    /// #670's shape on the phone: two channels under one name, only the second selected. Only the
    /// second may stream.
    /// </summary>
    [Fact]
    public void Of_two_channels_with_one_name_only_the_selected_one_streams()
    {
        Analog(1).Name = "AI0";
        SelectOnly(1);

        _shell.StreamToggleCommand.Execute(null);

        Assert.True(_shell.IsStreaming, $"Streaming did not start ({_shell.Status}).");
        Assert.Equal([1], ActiveAnalogIndexes());
    }

    /// <summary>A channel renamed while it streams keeps drawing on its own series.</summary>
    [Fact]
    public void A_channel_renamed_while_streaming_keeps_drawing()
    {
        SelectOnly(0);
        _shell.StreamToggleCommand.Execute(null);
        Assert.True(_shell.IsStreaming, $"Streaming did not start ({_shell.Status}).");
        var channel = Analog(0);

        Publish(channel, 1.0);
        _shell.PollActiveSamples();
        channel.Name = "Temp";
        Publish(channel, 2.0);
        _shell.PollActiveSamples();

        var series = Assert.Single(_shell.Series);
        var points = new double[series.Capacity];
        Assert.Equal(2, series.CopyTo(points));
        Assert.Equal([1.0, 2.0], points.Take(2));
    }

    private IChannel Analog(int index) =>
        _device.DataChannels.Single(c => c.Type == ChannelType.Analog && c.Index == index);

    private void SelectOnly(int index)
    {
        foreach (var toggle in _shell.Channels)
        {
            toggle.IsSelected = toggle.Index == index;
        }
    }

    private int[] ActiveAnalogIndexes() =>
        _device.DataChannels
            .Where(c => c.Type == ChannelType.Analog && !c.IsOutput && c.IsActive)
            .Select(c => c.Index)
            .OrderBy(i => i)
            .ToArray();

    private void Publish(IChannel channel, double value) =>
        channel.ActiveSample = new DataSample { Value = value, TimestampTicks = ++_ticks };
}
