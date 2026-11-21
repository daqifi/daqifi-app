using System.Reflection;
using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Core.Communication.Messages;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Logger;
using Xunit;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;
using CoreAnalogChannel = Daqifi.Core.Channel.AnalogChannel;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// #668: the live plot keys a series by the name its samples carry, which a session now fixes when it
/// starts. The plot also looks a series up from its subscribed channel to show or hide it, and that
/// lookup used the channel's current name. After a rename during the session it found nothing, so
/// the legend's show/hide no longer reached the renamed channel's line.
/// </summary>
/// <remarks>
/// On the app-host collection because the plot reads <c>LoggingManager.Instance</c>, and a channel
/// is subscribed there for the length of each test.
/// </remarks>
[Collection(AppHostCollection.Name)]
public sealed class LivePlotRenamedChannelVisibilityTests : IDisposable
{
    private const string Serial = "SN-668-VIS";

    private readonly BenchDevice _device = new() { Name = "Nq1", DeviceSerialNo = Serial };
    private readonly AnalogChannel _channel;
    private readonly PlotLogger _plot;

    public LivePlotRenamedChannelVisibilityTests()
    {
        Daqifi.Desktop.App.InitializeMobile();
        _channel = new AnalogChannel(_device, new CoreAnalogChannel(1)) { Name = "AI1" };
        _device.DataChannels.Add(_channel);
        _plot = new PlotLogger();

        LoggingManager.Instance.Subscribe(_channel);
        LoggingManager.FixRecordedNames([_channel]);   // what a session start does
        _channel.Name = "Temp";                         // renamed during the session
    }

    public void Dispose() => LoggingManager.Instance.Unsubscribe(_channel);

    /// <summary>A channel hidden before its first sample gets a hidden line.</summary>
    [Fact]
    public void A_renamed_channel_hidden_before_its_first_sample_gets_a_hidden_line()
    {
        _channel.IsVisible = false;

        _plot.Log(new DataSample(_device, _channel, DateTime.UtcNow, 1.0));
        _plot.DrainPendingSamples();

        Assert.False(_plot.LoggedChannels[(Serial, "AI1")].IsVisible);
    }

    /// <summary>Hiding a renamed channel on the legend hides its line at the next redraw.</summary>
    [Fact]
    public void Hiding_a_renamed_channel_hides_its_line()
    {
        _plot.Log(new DataSample(_device, _channel, DateTime.UtcNow, 1.0));
        _plot.DrainPendingSamples();
        var line = _plot.LoggedChannels[(Serial, "AI1")];
        Assert.True(line.IsVisible);

        _channel.IsVisible = false;
        RenderTick();

        Assert.False(line.IsVisible);
    }

    /// <summary>
    /// One tick of the plot's render timer, which is where it mirrors each channel's visibility onto
    /// its line. The timer does not run here, and the tick redraws only once its interval has passed
    /// since the last redraw, so that time is first moved back.
    /// </summary>
    private void RenderTick()
    {
        typeof(PlotLogger)
            .GetField("_lastUpdateMilliSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_plot, -1_000_000L);
        typeof(PlotLogger)
            .GetMethod("CompositionTargetRendering", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_plot, [null, EventArgs.Empty]);
    }

    /// <summary>A wrapper with no transport. Nothing here reads what it would send.</summary>
    private sealed class BenchDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message)
        {
        }
    }
}
