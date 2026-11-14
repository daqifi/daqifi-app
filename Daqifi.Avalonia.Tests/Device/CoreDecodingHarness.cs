using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Microsoft.Extensions.Logging.Abstractions;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// A real, connected Core <see cref="DaqifiStreamingDevice"/> and the app wrapper that reads it,
/// wired the way <c>AbstractStreamingDevice.Connect</c> wires them, with a way to hand Core a
/// stream frame as its consumer thread would.
/// </summary>
/// <remarks>
/// <para>
/// Every frame goes through Core's own decode: its frame gate, its first-frame hold, its timestamp
/// reconstruction and its analog and digital unpacking. What reaches the app channels is what
/// Core published on <c>SampleReceived</c>. Nothing about the decode is simulated, which is the
/// point of issue #440: the app no longer has a decode of its own to test in isolation.
/// </para>
/// <para>
/// <see cref="FrameFeedingCoreDevice.Feed"/> calls Core's protected
/// <c>OnStreamMessageReceived</c>, which runs the whole decode synchronously, so a frame is fully
/// processed by the time the call returns.
/// </para>
/// </remarks>
internal sealed class CoreDecodingHarness : IDisposable
{
    /// <summary>The device clock rate the status message reports: the 42 MHz current firmware uses.</summary>
    public const uint TicksPerSecond = 42_000_000;

    /// <summary>The counter value the first frame of a test carries. Arbitrary, but not zero.</summary>
    public const uint FirstCounter = 1_000_000;

    private CoreDecodingHarness(CapturingTransport transport, FrameFeedingCoreDevice core, ProbeDevice device)
    {
        Transport = transport;
        Core = core;
        Device = device;
    }

    public CapturingTransport Transport { get; }

    public FrameFeedingCoreDevice Core { get; }

    public ProbeDevice Device { get; }

    /// <summary>The rate sessions run at, in Hz.</summary>
    public int RateHz => Device.StreamingFrequency;

    /// <summary>Device-counter ticks in one sample period at <see cref="RateHz"/>.</summary>
    public uint TicksPerSample => TicksPerSecond / (uint)RateHz;

    /// <summary>
    /// Connects a Core device that reports <paramref name="analogPorts"/> analog inputs and
    /// <paramref name="digitalPorts"/> digital pins, none enabled, and wraps it.
    /// </summary>
    /// <param name="firmware">
    /// The firmware version Core reads to decide whether to hold the first frame of a session.
    /// Firmware below v3.6.0 latches the last frame of a stopped session (nyquist#535).
    /// </param>
    public static CoreDecodingHarness Create(
        uint analogPorts = 3, uint digitalPorts = 0, string firmware = "3.8.0", int rateHz = 100)
    {
        var transport = new CapturingTransport();
        var core = new FrameFeedingCoreDevice(transport);
        core.Connect();
        core.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = analogPorts,
            AnalogInRes = 65535,
            DigitalPortNum = digitalPorts,
            TimestampFreq = TicksPerSecond
        });
        core.Metadata.FirmwareVersion = firmware;
        core.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };

        var device = new ProbeDevice();
        device.Attach(core);
        device.StreamingFrequency = rateHz;
        transport.ClearSent();

        return new CoreDecodingHarness(transport, core, device);
    }

    /// <summary>The app's analog channel with this channel number, switched on.</summary>
    public AnalogChannel EnableAnalog(int channelNumber)
    {
        var channel = Device.DataChannels.OfType<AnalogChannel>().Single(c => c.Index == channelNumber);
        channel.IsActive = true;
        return channel;
    }

    /// <summary>The app's digital channel with this channel number, switched on.</summary>
    public DigitalChannel EnableDigital(int channelNumber)
    {
        var channel = Device.DataChannels.OfType<DigitalChannel>().Single(c => c.Index == channelNumber);
        channel.IsActive = true;
        return channel;
    }

    /// <summary>A frame carrying raw ADC counts, one per enabled analog channel in channel order.</summary>
    public static DaqifiOutMessage RawFrame(uint counter, params int[] counts)
    {
        var frame = new DaqifiOutMessage { MsgTimeStamp = counter };
        frame.AnalogInData.AddRange(counts);
        return frame;
    }

    /// <summary>A frame carrying pre-scaled volts, one per enabled analog channel in channel order.</summary>
    public static DaqifiOutMessage FloatFrame(uint counter, params float[] volts)
    {
        var frame = new DaqifiOutMessage { MsgTimeStamp = counter };
        frame.AnalogInDataFloat.AddRange(volts);
        return frame;
    }

    public void Dispose()
    {
        // Release the parked reader before Core tears the session down: its consumer-thread join
        // is bounded, but leaving the thread blocked spends that whole bound on every test.
        Transport.CloseStream();
        Core.Dispose();
        Transport.Dispose();
    }

    /// <summary>
    /// A real Core streaming device that can be handed a frame, and whose capability-document read
    /// answers at once with nothing instead of waiting three seconds for a board.
    /// </summary>
    internal sealed class FrameFeedingCoreDevice(CapturingTransport transport)
        : DaqifiStreamingDevice("core", transport, NullLogger.Instance)
    {
        /// <summary>Hands Core a stream frame, as its consumer thread does.</summary>
        public void Feed(DaqifiOutMessage frame) => OnStreamMessageReceived(frame);

        public override Task<CapabilityDocument?> ReadCapabilityDocumentAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<CapabilityDocument?>(null);
    }

    /// <summary>
    /// Minimal concrete <see cref="AbstractStreamingDevice"/>, attached to a Core device the way
    /// <c>Connect</c> attaches one.
    /// </summary>
    internal sealed class ProbeDevice : AbstractStreamingDevice
    {
        public ProbeDevice()
        {
            Name = "USB probe";
            DeviceSerialNo = "SN-PROBE";
        }

        public override ConnectionType ConnectionType => ConnectionType.Usb;

        /// <summary>Every device message the wrapper dispatched, in order.</summary>
        public List<DeviceMessage> DeviceMessages { get; } = [];

        /// <summary>
        /// Stands in for <c>LoggingManager.HandleDeviceMessage</c>, whose singleton needs the
        /// application service provider.
        /// </summary>
        public Action<DeviceMessage>? DeviceMessageDispatched { get; set; }

        protected override void SendMessage(IOutboundMessage<string> message)
        {
            // Nothing in these tests sends through the wrapper.
        }

        protected override void DispatchDeviceMessage(DeviceMessage deviceMessage)
        {
            DeviceMessages.Add(deviceMessage);
            DeviceMessageDispatched?.Invoke(deviceMessage);
        }

        /// <summary>
        /// What <c>Connect</c> does after Core's transport is open, less the initialization
        /// conversation with a board: subscribe, wire the protocol handler, and build the channel
        /// list from Core's channels.
        /// </summary>
        public void Attach(DaqifiStreamingDevice core)
        {
            CoreDevice = core;
            SubscribeCoreDeviceEvents(core);
            InitializeDeviceState();
            SyncFromCoreDevice(core);
        }

        /// <summary>Re-runs the channel sync a later status message triggers.</summary>
        public void Resync(DaqifiStreamingDevice core) => SyncFromCoreDevice(core);
    }
}
