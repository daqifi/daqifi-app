using Daqifi.Avalonia.Tests.Device;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Xunit;
using CoreAnalogChannel = Daqifi.Core.Channel.AnalogChannel;
using CoreDigitalChannel = Daqifi.Core.Channel.DigitalChannel;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Issue #548: a DISCONNECT while an SD card file downloads saved a corrupt session as Complete.
///
/// <para>
/// <c>AbstractStreamingDevice.Disconnect</c> clears <c>DataChannels</c> at once, but Core lets a
/// download that holds its operation lock finish first. The importer read the device's channels
/// only AFTER the download, so it found none, handed the parser no configuration, and the parser
/// fell back to raw ADC counts, no digital channels and a guessed 50 MHz clock. On the bench the
/// same 4.6 KB log gave 7,524 samples connected and 836 after a DISCONNECT, marked Complete.
/// </para>
///
/// <para>
/// The device fake here does what the disconnect does, at the same point: its download writes a
/// complete log and then empties <c>DataChannels</c> before it returns. The log itself states no
/// clock, no port counts and no calibration — like the bench board's firmware 3.8.0 log — so the
/// device's configuration is the only source of all three.
/// </para>
/// </summary>
public sealed class SdCardImportDeviceDisconnectTests : IDisposable
{
    private const string FileName = "log_20261110_120000.bin";

    /// <summary>The clock shipped Nyquist firmware reports; Core's guess is 50 MHz.</summary>
    private const uint DeviceClockHz = 42_000_000;

    /// <summary>Ten milliseconds at the device clock, 8.4 ms at the 50 MHz guess.</summary>
    private const uint TicksPerSample = 420_000;

    private const int SampleCount = 20;
    private const int AnalogCount = 2;
    private const int DigitalCount = 16;

    /// <summary>Half of a 12-bit range: 5.0 V on a 10 V port, with unit calibration.</summary>
    private const int RawCount = 2048;
    private const uint Resolution = 4096;
    private const double PortRange = 10.0;
    private const double ExpectedVolts = RawCount / (double)Resolution * PortRange;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "daqifi-avalonia-tests", "sd-import-disconnect-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public SdCardImportDeviceDisconnectTests()
    {
        Directory.CreateDirectory(_directory);
        DatabaseMigrator.ApplyMigrations(Factory(), DatabasePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    #region A disconnect during the download

    /// <summary>
    /// The defect. The file arrived complete, so the session must be the same one a connected
    /// import makes: every channel, values in volts, and the device's clock. Against the unfixed
    /// importer this kept AI0 and AI1 only, as raw counts, on the 50 MHz guess.
    /// </summary>
    [Fact]
    public async Task A_disconnect_during_the_download_still_imports_every_channel_in_volts_on_the_device_clock()
    {
        var device = Nyquist();
        device.OnDownload = (name, _) =>
        {
            var result = WriteLog(name);

            // What AbstractStreamingDevice.Disconnect does while Core finishes the download.
            device.DataChannels.Clear();
            device.IsConnected = false;
            return Task.FromResult(result);
        };

        var imported = await Importer().ImportFromDeviceAsync(device, FileName);

        Assert.True(imported.SessionPersisted);
        Assert.Equal(SampleCount * (AnalogCount + DigitalCount), imported.SamplesImported);

        var samples = SamplesFor(imported.Session.ID);

        var expectedChannels = Enumerable.Range(0, AnalogCount).Select(i => $"AI{i}")
            .Concat(Enumerable.Range(0, DigitalCount).Select(i => $"DI{i}"))
            .OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(expectedChannels, samples.Select(s => s.ChannelName).Distinct().OrderBy(n => n, StringComparer.Ordinal));

        Assert.All(
            samples.Where(s => s.ChannelName.StartsWith("AI", StringComparison.Ordinal)),
            s => Assert.Equal(ExpectedVolts, s.Value, precision: 6));

        var times = samples.Select(s => s.TimestampTicks).Distinct().Order().ToList();
        Assert.Equal(SampleCount, times.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(10).Ticks, times[1] - times[0]);

        // The device the file came from, not "Unknown".
        Assert.All(samples, s => Assert.Equal(device.DeviceSerialNo, s.DeviceSerialNo));
    }

    /// <summary>
    /// The control: the same file from a device that stays connected. It must give the same
    /// session as the case above, so the test above measures the disconnect and nothing else.
    /// </summary>
    [Fact]
    public async Task The_same_file_from_a_device_that_stays_connected_imports_the_same_session()
    {
        var device = Nyquist();
        device.OnDownload = (name, _) => Task.FromResult(WriteLog(name));

        var imported = await Importer().ImportFromDeviceAsync(device, FileName);

        Assert.True(imported.SessionPersisted);
        Assert.Equal(SampleCount * (AnalogCount + DigitalCount), imported.SamplesImported);
        var samples = SamplesFor(imported.Session.ID);
        Assert.All(
            samples.Where(s => s.ChannelName.StartsWith("AI", StringComparison.Ordinal)),
            s => Assert.Equal(ExpectedVolts, s.Value, precision: 6));
    }

    #endregion

    #region A device with no configuration at the start

    /// <summary>
    /// A device with no analog channels when the import starts has no configuration to give: it
    /// is disconnecting, or its channels were never read. A file parsed without one keeps raw
    /// counts and loses its digital channels, so the import must stop and say so — before it
    /// downloads anything, and without writing a session that claims to be complete.
    /// </summary>
    [Fact]
    public async Task A_device_with_no_channels_at_the_start_is_refused_before_the_download()
    {
        var device = new RecordingStreamingDevice { TimestampFrequency = DeviceClockHz };
        var downloads = 0;
        device.OnDownload = (name, _) =>
        {
            downloads++;
            return Task.FromResult(WriteLog(name));
        };

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => Importer().ImportFromDeviceAsync(device, FileName));

        Assert.Equal(0, downloads);
        using var context = Factory().CreateDbContext();
        Assert.Empty(context.Sessions.AsNoTracking().ToList());
    }

    #endregion

    #region Helpers

    /// <summary>
    /// A device configured like the bench Nyquist in the issue: two analog inputs and sixteen
    /// digital lines.
    /// </summary>
    private static RecordingStreamingDevice Nyquist()
    {
        var device = new RecordingStreamingDevice("SN-548") { TimestampFrequency = DeviceClockHz };

        for (var i = 0; i < AnalogCount; i++)
        {
            device.DataChannels.Add(new AnalogChannel(device, new CoreAnalogChannel(i, Resolution))
            {
                Name = $"AI{i}",
                PortRange = PortRange,
                CalibrationMValue = 1.0,
                CalibrationBValue = 0.0,
                InternalScaleMValue = 1.0
            });
        }

        for (var i = 0; i < DigitalCount; i++)
        {
            device.DataChannels.Add(new DigitalChannel(device, new CoreDigitalChannel(i)) { Name = $"DI{i}" });
        }

        return device;
    }

    /// <summary>
    /// Writes a protobuf SD log to the temp directory, where the importer requires a download to
    /// land. Every message is a sample: raw ADC counts, two bytes of digital lines and a device
    /// tick. Nothing in it states a clock, a port count or a calibration.
    /// </summary>
    private static SdCardDownloadResult WriteLog(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "daqifi-548-" + Guid.NewGuid().ToString("N") + ".bin");

        using (var stream = File.Create(path))
        {
            for (var i = 0; i < SampleCount; i++)
            {
                var message = new DaqifiOutMessage
                {
                    MsgTimeStamp = (uint)((i + 1) * TicksPerSample),
                    DigitalData = ByteString.CopyFrom(0x05, 0x00)
                };

                for (var ch = 0; ch < AnalogCount; ch++)
                {
                    message.AnalogInData.Add(RawCount);
                }

                message.WriteDelimitedTo(stream);
            }
        }

        return new SdCardDownloadResult(name, new FileInfo(path).Length, TimeSpan.Zero, path);
    }

    private SdCardSessionImporter Importer() => new(Factory());

    private IDbContextFactory<LoggingContext> Factory() => TestDatabase.Contexts(DatabasePath);

    private List<DataSample> SamplesFor(int sessionId)
    {
        using var context = Factory().CreateDbContext();
        return context.Samples.AsNoTracking().Where(s => s.LoggingSessionID == sessionId).ToList();
    }

    #endregion
}
