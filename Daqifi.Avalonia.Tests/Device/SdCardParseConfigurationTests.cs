using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Device.SdCard;
using Xunit;
using UsbStreamingDevice = Daqifi.Desktop.Device.UsbDevice.UsbStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins the configuration that a connected device gives the SD card importer, for each board and
/// channel set: the channel counts, the timestamp clock, the identity, and the calibration,
/// resolution, port range and internal scale of each analog channel (#671).
/// </summary>
/// <remarks>
/// <para>
/// Each row connects a real <see cref="UsbStreamingDevice"/> to a real Core device over a scripted
/// board (<see cref="ScriptedReplyTransport"/>, from #669). So the status message goes through
/// Core's own parse, and the wrapper builds its channels exactly as it does from a board.
/// </para>
/// <para>
/// The expected values come from the status message the row sends, not from Core. If the value
/// that reaches the parser changes, a row fails.
/// </para>
/// </remarks>
public class SdCardParseConfigurationTests
{
    private const uint DeviceClockHz = 42_000_000;

    public static TheoryData<string> Boards => new()
    {
        Nq1,
        Nq1WithoutClockOrCalibration,
        Nq2,
        Nq3WithAnalogOutputs,
    };

    private const string Nq1 = "Nq1: 16 AI, 16 DIO, clock and calibration stated";
    private const string Nq1WithoutClockOrCalibration = "Nq1: 16 AI, 16 DIO, no clock, no calibration (#464)";
    private const string Nq2 = "Nq2: 8 AI, 8 DIO, counts from the status only";
    private const string Nq3WithAnalogOutputs = "Nq3: 8 AI, 16 DIO, 8 AO from the capability document";

    [Theory]
    [MemberData(nameof(Boards))]
    public void The_configuration_is_the_one_the_device_reported(string board)
    {
        var (status, document, expected) = Row(board);
        var device = new InspectableUsbDevice(ScriptedReplyTransport.Board(status));
        try
        {
            Assert.True(device.Connect(), device.LastConnectError?.ToString());

            if (document != null)
            {
                // What Core does when it reads the document (DaqifiDevice.TryApplyCapabilityDocument):
                // the analog outputs exist only there, and Core announces them with ChannelsPopulated.
                device.Core.Metadata.ApplyCapabilityDocument(document);
                device.Core.SyncAnalogOutputChannelsFromCapabilities();

                // Guards the row: the Core device really holds channels that are neither analog
                // inputs nor digital, so the counts below are a real test of the filter.
                Assert.Equal(
                    expected.AnalogOutputCount,
                    device.Core.GetChannelsSnapshot().Count(c => c.Type == ChannelType.AnalogOutput));
            }

            var configuration = device.GetSdCardParseConfiguration();

            AssertConfiguration(expected, configuration);

            // The serial the importer writes into every sample when the log states none must be the
            // serial the rest of the app shows for this device.
            Assert.Equal(device.DeviceSerialNo, configuration!.DeviceSerialNumber);
        }
        finally
        {
            device.Disconnect();
        }
    }

    /// <summary>
    /// A device with no analog channels has nothing to calibrate, so it has no configuration. The
    /// importer refuses such a device before it downloads anything (#548).
    /// </summary>
    [Fact]
    public void A_device_with_no_analog_channels_has_no_configuration()
    {
        var status = new DaqifiOutMessage
        {
            DevicePn = "Nq1",
            DeviceFwRev = "3.4.0",
            DeviceSn = 1001,
            TimestampFreq = DeviceClockHz,
            DigitalPortNum = 16
        };
        var device = new InspectableUsbDevice(ScriptedReplyTransport.Board(status));
        try
        {
            Assert.True(device.Connect(), device.LastConnectError?.ToString());
            Assert.Equal(16, device.DataChannels.Count);

            Assert.Null(device.GetSdCardParseConfiguration());
        }
        finally
        {
            device.Disconnect();
        }
    }

    /// <summary>
    /// No Core device, no configuration: before the first connect and after a disconnect. This is
    /// what makes the importer refuse a device that is going away.
    /// </summary>
    [Fact]
    public void A_device_that_is_not_connected_has_no_configuration()
    {
        var (status, _, _) = Row(Nq1);
        var device = new InspectableUsbDevice(ScriptedReplyTransport.Board(status));

        Assert.Null(device.GetSdCardParseConfiguration());

        Assert.True(device.Connect(), device.LastConnectError?.ToString());
        Assert.NotNull(device.GetSdCardParseConfiguration());

        device.Disconnect();

        Assert.Null(device.GetSdCardParseConfiguration());
    }

    #region Rows

    private static (DaqifiOutMessage Status, CapabilityDocument? Document, Expected Expected) Row(string board) => board switch
    {
        Nq1 => Nq1Row(),
        Nq1WithoutClockOrCalibration => Nq1WithoutClockOrCalibrationRow(),
        Nq2 => Nq2Row(),
        Nq3WithAnalogOutputs => Nq3Row(),
        _ => throw new ArgumentOutOfRangeException(nameof(board), board, null)
    };

    /// <summary>
    /// A 12-bit board whose firmware states its clock and a calibration per channel. The values
    /// are different on each channel, so a row in the wrong order fails.
    /// </summary>
    private static (DaqifiOutMessage, CapabilityDocument?, Expected) Nq1Row()
    {
        float[] slopes = [1.00213f, 0.99871f, 1.00042f, 0.99695f, 1.00318f, 0.99924f, 1.00107f, 0.99788f,
                          1.00256f, 0.99902f, 1.00081f, 0.99733f, 1.00194f, 0.99857f, 1.00029f, 0.99966f];
        float[] intercepts = [-0.0031f, 0.0024f, -0.0012f, 0.0047f, -0.0008f, 0.0019f, -0.0026f, 0.0035f,
                              -0.0041f, 0.0006f, -0.0017f, 0.0029f, -0.0003f, 0.0052f, -0.0022f, 0.0011f];
        float[] ranges = [10f, 5f, 10f, 5f, 10f, 5f, 10f, 5f, 10f, 5f, 10f, 5f, 10f, 5f, 10f, 5f];
        float[] scales = [1f, 2f, 1f, 2f, 1f, 2f, 1f, 2f, 1f, 2f, 1f, 2f, 1f, 2f, 1f, 2f];

        var status = new DaqifiOutMessage
        {
            DevicePn = "Nq1",
            DeviceFwRev = "3.4.0",
            DeviceSn = 4788544735461581972,
            TimestampFreq = DeviceClockHz,
            AnalogInPortNum = 16,
            AnalogInRes = 4095,
            DigitalPortNum = 16
        };
        status.AnalogInCalM.AddRange(slopes);
        status.AnalogInCalB.AddRange(intercepts);
        status.AnalogInPortRange.AddRange(ranges);
        status.AnalogInIntScaleM.AddRange(scales);

        return (status, null, new Expected(
            AnalogPortCount: 16,
            DigitalPortCount: 16,
            TimestampFrequency: DeviceClockHz,
            DeviceSerialNumber: "4788544735461581972",
            DevicePartNumber: "Nq1",
            FirmwareRevision: "3.4.0",
            CalibrationValues: slopes.Zip(intercepts, (m, b) => ((double)m, (double)b)).ToArray(),
            Resolution: 4095,
            PortRange: ranges.Select(r => (double)r).ToArray(),
            InternalScaleM: scales.Select(s => (double)s).ToArray()));
    }

    /// <summary>
    /// The same board on firmware that states no clock and no calibration. The clock stays 0, so
    /// the parser's own fallback applies (#464), and each channel has Core's defaults.
    /// </summary>
    private static (DaqifiOutMessage, CapabilityDocument?, Expected) Nq1WithoutClockOrCalibrationRow()
    {
        var status = new DaqifiOutMessage
        {
            DevicePn = "Nq1",
            DeviceFwRev = "3.4.0",
            DeviceSn = 4788544735461581972,
            AnalogInPortNum = 16,
            AnalogInRes = 4095,
            DigitalPortNum = 16
        };

        return (status, null, new Expected(
            AnalogPortCount: 16,
            DigitalPortCount: 16,
            TimestampFrequency: 0,
            DeviceSerialNumber: "4788544735461581972",
            DevicePartNumber: "Nq1",
            FirmwareRevision: "3.4.0",
            CalibrationValues: Enumerable.Repeat((1.0, 0.0), 16).ToArray(),
            Resolution: 4095,
            PortRange: Enumerable.Repeat(1.0, 16).ToArray(),
            InternalScaleM: Enumerable.Repeat(1.0, 16).ToArray()));
    }

    /// <summary>
    /// Core's board table has no channel counts for an Nq2 (the firmware has no Nq2 build), so the
    /// counts here can only come from the status message. They are illustrative, not an Nq2's
    /// real counts.
    /// </summary>
    private static (DaqifiOutMessage, CapabilityDocument?, Expected) Nq2Row()
    {
        float[] slopes = [1.0012f, 0.9987f, 1.0005f, 0.9991f, 1.0021f, 0.9978f, 1.0009f, 0.9996f];
        float[] intercepts = [0.0012f, -0.0007f, 0.0003f, -0.0019f, 0.0008f, -0.0002f, 0.0015f, -0.0011f];

        var status = new DaqifiOutMessage
        {
            DevicePn = "Nq2",
            DeviceFwRev = "3.4.0",
            DeviceSn = 2002,
            TimestampFreq = DeviceClockHz,
            AnalogInPortNum = 8,
            AnalogInRes = 65535,
            DigitalPortNum = 8
        };
        status.AnalogInCalM.AddRange(slopes);
        status.AnalogInCalB.AddRange(intercepts);
        status.AnalogInPortRange.AddRange(Enumerable.Repeat(5f, 8));
        status.AnalogInIntScaleM.AddRange(Enumerable.Repeat(1f, 8));

        return (status, null, new Expected(
            AnalogPortCount: 8,
            DigitalPortCount: 8,
            TimestampFrequency: DeviceClockHz,
            DeviceSerialNumber: "2002",
            DevicePartNumber: "Nq2",
            FirmwareRevision: "3.4.0",
            CalibrationValues: slopes.Zip(intercepts, (m, b) => ((double)m, (double)b)).ToArray(),
            Resolution: 65535,
            PortRange: Enumerable.Repeat(5.0, 8).ToArray(),
            InternalScaleM: Enumerable.Repeat(1.0, 8).ToArray()));
    }

    /// <summary>
    /// An 18-bit board with 8 analog inputs, 16 digital lines and, from its capability document,
    /// 8 analog outputs (Core's Nyquist3 board table). The outputs are channels in the Core device,
    /// but they are not inputs, so they must not count as analog ports or carry a calibration.
    /// </summary>
    private static (DaqifiOutMessage, CapabilityDocument?, Expected) Nq3Row()
    {
        float[] slopes = [1.00004f, 0.99997f, 1.00011f, 0.99989f, 1.00006f, 0.99993f, 1.00015f, 0.99982f];
        float[] intercepts = [0.00021f, -0.00013f, 0.00007f, -0.00031f, 0.00018f, -0.00004f, 0.00026f, -0.00009f];

        var status = new DaqifiOutMessage
        {
            DevicePn = "Nq3",
            DeviceFwRev = "3.4.0",
            DeviceSn = 3003,
            TimestampFreq = DeviceClockHz,
            AnalogInPortNum = 8,
            AnalogInRes = 262143,
            DigitalPortNum = 16
        };
        status.AnalogInCalM.AddRange(slopes);
        status.AnalogInCalB.AddRange(intercepts);
        status.AnalogInPortRange.AddRange(Enumerable.Repeat(10f, 8));
        status.AnalogInIntScaleM.AddRange(Enumerable.Repeat(1f, 8));

        var channels = new List<CapabilityChannel>();
        channels.AddRange(Enumerable.Range(0, 8).Select(i => new CapabilityChannel { Id = i, Kind = CapabilityChannelKind.AnalogInput }));
        channels.AddRange(Enumerable.Range(0, 8).Select(i => new CapabilityChannel { Id = i, Kind = CapabilityChannelKind.AnalogOutput }));
        channels.AddRange(Enumerable.Range(0, 16).Select(i => new CapabilityChannel { Id = i, Kind = CapabilityChannelKind.DigitalIo }));
        var document = new CapabilityDocument { SchemaVersion = 2, Channels = channels };

        return (status, document, new Expected(
            AnalogPortCount: 8,
            DigitalPortCount: 16,
            TimestampFrequency: DeviceClockHz,
            DeviceSerialNumber: "3003",
            DevicePartNumber: "Nq3",
            FirmwareRevision: "3.4.0",
            CalibrationValues: slopes.Zip(intercepts, (m, b) => ((double)m, (double)b)).ToArray(),
            Resolution: 262143,
            PortRange: Enumerable.Repeat(10.0, 8).ToArray(),
            InternalScaleM: Enumerable.Repeat(1.0, 8).ToArray(),
            AnalogOutputCount: 8));
    }

    #endregion

    #region Helpers

    private sealed record Expected(
        int AnalogPortCount,
        int DigitalPortCount,
        uint TimestampFrequency,
        string DeviceSerialNumber,
        string DevicePartNumber,
        string FirmwareRevision,
        (double Slope, double Intercept)[] CalibrationValues,
        uint Resolution,
        double[] PortRange,
        double[] InternalScaleM,
        int AnalogOutputCount = 0);

    /// <summary>
    /// Field by field. The record's own equality compares the three lists by reference, so it
    /// cannot tell two equal configurations apart from two different ones.
    /// </summary>
    private static void AssertConfiguration(Expected expected, SdCardDeviceConfiguration? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.AnalogPortCount, actual.AnalogPortCount);
        Assert.Equal(expected.DigitalPortCount, actual.DigitalPortCount);
        Assert.Equal(expected.TimestampFrequency, actual.TimestampFrequency);
        Assert.Equal(expected.DeviceSerialNumber, actual.DeviceSerialNumber);
        Assert.Equal(expected.DevicePartNumber, actual.DevicePartNumber);
        Assert.Equal(expected.FirmwareRevision, actual.FirmwareRevision);
        Assert.Equal(expected.CalibrationValues, Assert.IsAssignableFrom<IReadOnlyList<(double, double)>>(actual.CalibrationValues));
        Assert.Equal(expected.Resolution, actual.Resolution);
        Assert.Equal(expected.PortRange, Assert.IsAssignableFrom<IReadOnlyList<double>>(actual.PortRange));
        Assert.Equal(expected.InternalScaleM, Assert.IsAssignableFrom<IReadOnlyList<double>>(actual.InternalScaleM));
    }

    /// <summary>
    /// Gives the Nq3 row the Core device, so it can apply a capability document the way Core does.
    /// </summary>
    private sealed class InspectableUsbDevice(IStreamTransport transport) : UsbStreamingDevice(transport, "DAQiFi")
    {
        public DaqifiStreamingDevice Core => CoreDevice ?? throw new InvalidOperationException("Not connected.");
    }

    #endregion
}
