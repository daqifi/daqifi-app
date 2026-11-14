using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Desktop.Device;
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
            Metadata.ApplyCapabilityDocument(new CapabilityDocument
            {
                SchemaVersion = 2,
                Channels =
                [
                    new CapabilityChannel
                    {
                        Id = 0,
                        Kind = CapabilityChannelKind.DigitalIo,
                        SupportsPwm = true,
                        PwmMinimumFrequencyHz = FloorHz
                    }
                ]
            });

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
