using System.ComponentModel;
using Daqifi.Avalonia.Tests.Device;
using Daqifi.Desktop.Channel;
using Xunit;

namespace Daqifi.Avalonia.Tests.Channels;

/// <summary>
/// Pins that a streamed sample costs the UI thread nothing (#620).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AbstractChannel.ActiveSample"/> is assigned on the device reader thread once per
/// sample. Avalonia marshals every <c>PropertyChanged</c> that a background thread raises to the UI
/// thread before its bindings filter by property name, and the Live Graph legend binds the channel
/// object. So a <c>PropertyChanged("ActiveSample")</c> was one empty UI job per sample: about 16,900
/// a second at 16 channels x 1 kHz. Nothing reads that notification.
/// </para>
/// <para>
/// The two events that real consumers use must still fire for every sample:
/// <see cref="AbstractChannel.OnChannelUpdated"/> (the loggers, the live plot) and
/// <see cref="AbstractChannel.SampleReceived"/> (the phone's silent-stream watchdog).
/// </para>
/// </remarks>
public class ActiveSampleNotificationTests
{
    [Fact]
    public async Task A_sample_assigned_on_a_background_thread_raises_no_PropertyChanged()
    {
        var channel = new FakeChannel("AI0", "SN-620");
        var raised = new List<string?>();
        ((INotifyPropertyChanged)channel).PropertyChanged += (_, e) =>
        {
            lock (raised) { raised.Add(e.PropertyName); }
        };

        await Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                channel.ActiveSample = new DataSample { Value = i };
            }
        });

        Assert.Empty(raised);
        Assert.Equal(999, channel.ActiveSample!.Value);
    }

    [Fact]
    public async Task Every_sample_still_reaches_the_loggers_and_the_watchdog()
    {
        var channel = new FakeChannel("AI0", "SN-620");
        var updates = 0;
        var received = 0;
        channel.OnChannelUpdated += (_, _) => Interlocked.Increment(ref updates);
        channel.SampleReceived += (_, _) => Interlocked.Increment(ref received);

        await Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                channel.ActiveSample = new DataSample { Value = i };
            }
        });

        Assert.Equal(1000, updates);
        Assert.Equal(1000, received);
    }
}
