using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Desktop.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins the second <c>HydrateDeviceMetadata</c> call in <see cref="AbstractStreamingDevice.Connect"/>
/// — the one that runs after Core's <c>InitializeAsync()</c> returns.
///
/// It exists because Core learns the device's own capabilities in two stages and only announces the
/// first. <c>ChannelsPopulated</c> is raised from the device's first status message, which carries
/// the board's part number and channel counts and nothing else; the capability document — the only
/// thing in <c>Daqifi.Core</c> 1.7.0 that can move <c>DeviceCapabilities</c> off the board table,
/// <c>MaxSamplingRate</c> included — is read afterwards, by <c>ReadCapabilityDocumentAsync</c>.
/// Core re-raises <c>ChannelsPopulated</c> for that document only when it adds analog-output
/// channels, so on a board that has none nothing tells the wrapper the document arrived. The
/// post-initialize hydrate is what does.
///
/// Measured on the bench Nq1 (fw 3.7.2, USB) against the pinned package: at the single
/// <c>ChannelsPopulated</c> raise the metadata held <c>MaxSamplingRate</c> 1000 and a null
/// <c>CapabilityDocument</c>; when <c>InitializeAsync()</c> returned it held the device's own
/// schema-2 document and <c>MaxSamplingRate</c> 22000, from that document's
/// <c>streaming.sample_rate_range_hz.max</c>. That ordering is what the double below replays.
///
/// Core 1.10.0 (daqifi-core#907) moved the Nyquist1 board-table ceiling to 22000, the value the
/// bench document states, so the bench figures can no longer tell the two hydrates apart. The
/// document below therefore states a LOWER ceiling than the board table: a value a device is free
/// to state (fewer channels or a slower build), and one the first hydrate cannot have copied.
/// </summary>
public class AbstractStreamingDeviceMetadataHydrationTests
{
    /// <summary>
    /// What <c>DeviceCapabilities.FromDeviceType</c> returns for a Nyquist1 in the pinned Core
    /// (1.10.0, daqifi-core#907; it was 1000 before): the bootstrap value, used before the device
    /// has been asked anything and kept for firmware that cannot answer.
    /// </summary>
    private const int BoardTableCeilingHz = 22000;

    /// <summary>
    /// The ceiling this test's capability document states. A device property read back from the
    /// board, not a limit this app or Core defines — a unit or a firmware is free to state a value
    /// other than the board table, which is the whole reason the wrapper reads it instead of
    /// assuming. It differs from <see cref="BoardTableCeilingHz"/> so that the assertions can tell
    /// which hydrate a value came from.
    /// </summary>
    private const int DocumentCeilingHz = 4000;

    [Fact]
    public void The_capability_document_reaches_the_wrapper_only_after_initialization()
    {
        DeviceCapabilities? capabilitiesAtChannelsPopulated = null;
        CapabilityDocument? documentAtChannelsPopulated = null;

        var device = new TestDevice(CoreDeviceThatLearns(LowerCeilingDocument()));
        device.CoreDeviceDouble.AfterChannelsPopulated = () =>
        {
            capabilitiesAtChannelsPopulated = device.Metadata.Capabilities;
            documentAtChannelsPopulated = device.Metadata.CapabilityDocument;
        };

        Assert.True(device.Connect());

        // The first hydrate ran and did its job — the board table reached the wrapper — but the
        // device's own answers were not available yet.
        Assert.NotNull(capabilitiesAtChannelsPopulated);
        Assert.Equal(DeviceType.Nyquist1, device.Metadata.DeviceType);
        Assert.Equal(BoardTableCeilingHz, capabilitiesAtChannelsPopulated!.MaxSamplingRate);
        Assert.Null(documentAtChannelsPopulated);

        // And the second hydrate is what carried them across.
        Assert.NotNull(device.Metadata.CapabilityDocument);
        Assert.Equal(DocumentCeilingHz, device.Metadata.Capabilities.MaxSamplingRate);
    }

    [Fact]
    public void The_ceiling_the_rate_guard_clamps_against_is_the_one_the_document_states()
    {
        // Why the hydrate is not merely tidy: AbstractStreamingDevice.StreamingFrequency clamps to
        // Capabilities.MaxSamplingRate, so a wrapper still holding the board table would accept a
        // rate this device says it cannot sustain (or, with a higher document ceiling, refuse one
        // it can).
        var device = new TestDevice(CoreDeviceThatLearns(LowerCeilingDocument()));
        Assert.True(device.Connect());

        device.StreamingFrequency = 5000;

        Assert.Equal(DocumentCeilingHz, device.StreamingFrequency);
    }

    [Fact]
    public void A_board_that_publishes_no_document_keeps_the_board_table_ceiling()
    {
        // The control, and the common case on firmware below v3.5.0: nothing to overlay, so the
        // second hydrate re-copies what the first one already brought and the ceiling stays put.
        var device = new TestDevice(CoreDeviceThatLearns(document: null));
        Assert.True(device.Connect());

        device.StreamingFrequency = BoardTableCeilingHz + 5000;

        Assert.Null(device.Metadata.CapabilityDocument);
        Assert.Equal(BoardTableCeilingHz, device.Metadata.Capabilities.MaxSamplingRate);
        Assert.Equal(BoardTableCeilingHz, device.StreamingFrequency);
    }

    /// <summary>
    /// A schema-2 document reduced to the field this test turns on. Built directly rather than
    /// parsed so the test states what it depends on; the parse itself is Core's to cover.
    /// </summary>
    private static CapabilityDocument LowerCeilingDocument() => new()
    {
        SchemaVersion = 2,
        Streaming = new CapabilityStreaming { MaximumSampleRateHz = DocumentCeilingHz }
    };

    private static LearningCoreDevice CoreDeviceThatLearns(CapabilityDocument? document) =>
        new("core") { Document = document };

    /// <summary>
    /// A Core device that replays what a real one learns during <c>InitializeAsync()</c>, in the
    /// order a real one learns it: the status message first (raising <c>ChannelsPopulated</c>
    /// through Core's own <see cref="DaqifiDevice.PopulateChannelsFromStatus"/>), the capability
    /// document afterwards.
    /// </summary>
    /// <remarks>
    /// The real <c>InitializeAsync</c> is a SCPI conversation with a live board, so it is the one
    /// part of this path a unit test cannot own. Everything the assertions read — the metadata
    /// copy, the event wiring, the connect template — is production code.
    /// </remarks>
    private sealed class LearningCoreDevice(string name) : DaqifiStreamingDevice(name)
    {
        public CapabilityDocument? Document { get; init; }

        /// <summary>Runs once <c>ChannelsPopulated</c> has been delivered to its subscribers.</summary>
        public Action? AfterChannelsPopulated { get; set; }

        public override Task InitializeAsync(
            TimeSpan? channelPopulationTimeout = null,
            CancellationToken cancellationToken = default)
        {
            var status = new DaqifiOutMessage
            {
                DevicePn = "Nq1",
                DeviceFwRev = "3.7.2",
                AnalogInPortNum = 16,
                AnalogInRes = 4095,
                DigitalPortNum = 16
            };

            Metadata.UpdateFromProtobuf(status);
            PopulateChannelsFromStatus(status);
            AfterChannelsPopulated?.Invoke();

            if (Document != null)
            {
                Metadata.ApplyCapabilityDocument(Document);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal concrete <see cref="AbstractStreamingDevice"/> whose Core device is supplied rather
    /// than opened, so <see cref="AbstractStreamingDevice.Connect"/> runs its real template with no
    /// transport involved.
    /// </summary>
    private sealed class TestDevice(LearningCoreDevice coreDevice) : AbstractStreamingDevice
    {
        public LearningCoreDevice CoreDeviceDouble { get; } = coreDevice;

        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override DaqifiStreamingDevice CreateCoreDevice() => CoreDeviceDouble;

        protected override void SendMessage(IOutboundMessage<string> message) =>
            throw new NotSupportedException();
    }
}
