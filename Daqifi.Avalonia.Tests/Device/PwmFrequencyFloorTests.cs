using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Desktop.Device;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppDigitalChannel = Daqifi.Desktop.Channel.DigitalChannel;
using CoreDigitalChannel = Daqifi.Core.Channel.DigitalChannel;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Issue #584: the Core 1.10.0 digital-pin changes the app must follow.
///
/// Core 1.10.0 refuses a PWM frequency below the floor a firmware 3.9.0 device advertises
/// (daqifi-core#963). The app held the frequency to the fixed 6 Hz constant, so a value between 6 Hz
/// and the device's floor reached Core and was refused, with the error only in the log.
///
/// Core 1.10.0 also reports a PWM pin's direction as Output while PWM runs (daqifi-core#951), and
/// <c>ReplaceCoreChannel</c> copied that value back onto the same Core object, which replaced the
/// direction the pin returns to when PWM stops.
/// </summary>
public class PwmFrequencyFloorTests
{
    /// <summary>
    /// A floor above the fixed 6 Hz. Real units compute theirs from the peripheral clock
    /// (ceil(PBCLK3 / 256 / 65535)), so a few hertz is typical; a larger value makes the clamp
    /// unmistakable in the assertions.
    /// </summary>
    private const int AdvertisedFloorHz = 50;

    [Fact]
    public void The_frequency_is_held_to_the_floor_the_device_advertises()
    {
        var device = new TestDevice(CoreDeviceOn("3.9.0", AdvertisedFloorHz));
        Assert.True(device.Connect());

        Assert.Equal(AdvertisedFloorHz, device.PwmFrequencyFloorHz);

        device.PwmFrequencyHz = 8;

        Assert.Equal(AdvertisedFloorHz, device.PwmFrequencyHz);
    }

    [Fact]
    public void Firmware_below_3_9_0_keeps_the_fixed_floor()
    {
        // Older firmware advertises a fixed 1 Hz it cannot produce, so Core keeps 6 Hz for it.
        var device = new TestDevice(CoreDeviceOn("3.8.0", AdvertisedFloorHz));
        Assert.True(device.Connect());

        Assert.Equal(DaqifiStreamingDevice.MinPwmFrequencyHz, device.PwmFrequencyFloorHz);

        device.PwmFrequencyHz = 8;

        Assert.Equal(8, device.PwmFrequencyHz);
    }

    [Fact]
    public void A_device_with_no_Core_device_states_the_fixed_floor()
    {
        var device = new TestDevice(CoreDeviceOn("3.9.0", AdvertisedFloorHz));

        Assert.Equal(DaqifiStreamingDevice.MinPwmFrequencyHz, device.PwmFrequencyFloorHz);
    }

    [Fact]
    public void A_repopulate_during_PWM_keeps_the_direction_the_pin_returns_to()
    {
        var core = new CoreDigitalChannel(channelNumber: 3, isPwmCapable: true);
#pragma warning disable CS0618 // Core-side bookkeeping only: this sets up the state under test.
        core.Direction = ChannelDirection.Input;
        core.IsPwmEnabled = true;
#pragma warning restore CS0618
        var wrapper = new AppDigitalChannel(new RecordingStreamingDevice(), core);
        Assert.Equal(ChannelDirection.Output, core.Direction); // Core masks it while PWM runs.

        // Core repopulates channels in place, so a status message hands back the same object.
        wrapper.ReplaceCoreChannel(core);

#pragma warning disable CS0618
        core.IsPwmEnabled = false;
#pragma warning restore CS0618
        Assert.Equal(ChannelDirection.Input, core.Direction);
        Assert.Equal(ChannelDirection.Input, wrapper.Direction);
    }

    /// <summary>
    /// Review of PR #592: a frequency stored before the device's floor was known (here, set while
    /// no Core device was attached) must not reach Core below the floor when PWM is enabled. Core
    /// 1.10.0 would refuse it, and the enable would stop there with the error only in the log. This
    /// drives a real Core device over a capturing transport and reads what reached the wire.
    /// </summary>
    [Fact]
    public void Enabling_PWM_sends_the_floor_when_the_stored_frequency_is_below_it()
    {
        var transport = new CapturingTransport();
        var core = new DaqifiStreamingDevice("TestDevice", transport, NullLogger.Instance);
        try
        {
            core.Connect();
            var status = new DaqifiOutMessage
            {
                DevicePn = "Nq1",
                DeviceFwRev = "3.9.0",
                AnalogInPortNum = 16,
                AnalogInRes = 4095,
                DigitalPortNum = 16
            };
            core.Metadata.UpdateFromProtobuf(status);
            core.PopulateChannelsFromStatus(status);
            core.Metadata.ApplyCapabilityDocument(FloorDocument(AdvertisedFloorHz));
            Assert.Equal(AdvertisedFloorHz, core.PwmFrequencyFloorHz);

            var device = new AttachableDevice();
            device.PwmFrequencyHz = 8; // No Core device yet, so the fixed 6 Hz floor applies.
            Assert.Equal(8, device.PwmFrequencyHz);
            device.Attach(core);

            var coreChannel = core.Channels.OfType<IDigitalChannel>().First(c => c.IsPwmCapable);
            var channel = new AppDigitalChannel(device, coreChannel);
            transport.ClearSent();

            device.SetChannelPwmEnabled(channel, true);

            var enable = $"PWM:CHannel:ENable {coreChannel.ChannelNumber},1";
            Assert.True(transport.WaitForSentText(enable, TimeSpan.FromSeconds(5)), transport.SentText);
            Assert.Contains($",{AdvertisedFloorHz}", FrequencyCommands(transport.SentText));
            Assert.DoesNotContain(",8", FrequencyCommands(transport.SentText));
            Assert.Equal(AdvertisedFloorHz, device.PwmFrequencyHz);
            Assert.True(coreChannel.IsPwmEnabled);
        }
        finally
        {
            transport.CloseStream();
            core.Dispose();
            transport.Dispose();
        }
    }

    private static string FrequencyCommands(string sent) =>
        string.Join('\n', sent.Split('\n').Where(line => line.StartsWith("PWM:CHannel:FREQuency", StringComparison.Ordinal)));

    private static CapabilityDocument FloorDocument(int floorHz) => new()
    {
        SchemaVersion = 2,
        Channels =
        [
            new CapabilityChannel
            {
                Id = 0,
                Kind = CapabilityChannelKind.DigitalIo,
                SupportsPwm = true,
                PwmMinimumFrequencyHz = floorHz
            }
        ]
    };

    /// <summary>A wrapper whose Core device is attached by the test, already connected.</summary>
    private sealed class AttachableDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        public void Attach(DaqifiStreamingDevice core) => CoreDevice = core;

        protected override void SendMessage(IOutboundMessage<string> message) =>
            throw new NotSupportedException();
    }

    private static FloorCoreDevice CoreDeviceOn(string firmware, int floorHz) =>
        new("core") { Firmware = firmware, FloorHz = floorHz };

    /// <summary>
    /// A Core device that learns, during <c>InitializeAsync()</c>, a firmware version and a capability
    /// document whose PWM-capable digital channel states <see cref="FloorHz"/>.
    /// </summary>
    private sealed class FloorCoreDevice(string name) : DaqifiStreamingDevice(name)
    {
        public required string Firmware { get; init; }

        public required int FloorHz { get; init; }

        public override Task InitializeAsync(
            TimeSpan? channelPopulationTimeout = null,
            CancellationToken cancellationToken = default)
        {
            var status = new DaqifiOutMessage
            {
                DevicePn = "Nq1",
                DeviceFwRev = Firmware,
                AnalogInPortNum = 16,
                AnalogInRes = 4095,
                DigitalPortNum = 16
            };

            Metadata.UpdateFromProtobuf(status);
            PopulateChannelsFromStatus(status);
            Metadata.ApplyCapabilityDocument(FloorDocument(FloorHz));

            return Task.CompletedTask;
        }
    }

    private sealed class TestDevice(FloorCoreDevice coreDevice) : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override DaqifiStreamingDevice CreateCoreDevice() => coreDevice;

        protected override void SendMessage(IOutboundMessage<string> message) =>
            throw new NotSupportedException();
    }
}
