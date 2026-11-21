using System.Globalization;
using System.Reflection;
using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Avalonia.Views;
using Daqifi.Core.Communication.Messages;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Exporter;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;
using CoreAnalogChannel = Daqifi.Core.Channel.AnalogChannel;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// #668 and #670: the live plot, the database, the CSV export and Logged Data all group a session's
/// samples by the channel name each sample carries, and that name was the channel's editable name at
/// the moment the sample arrived. A rename during logging (the NAME box writes on every keystroke)
/// split one input into one column per keystroke, Logged Data stopped the trace at the rename, and an
/// intermediate name equal to another channel's name put this input's readings into that channel's
/// column. Two channels the user gave one name shared one column.
/// </summary>
/// <remarks>
/// <para>
/// Every row runs the whole recording path for real: <see cref="AnalogChannel"/>s on a device
/// wrapper, samples built by the production <see cref="DataSample"/> constructor and published
/// through <c>ActiveSample</c>, a real <see cref="LoggingManager"/> with a real
/// <see cref="DatabaseLogger"/> over a migrated SQLite file and a real <see cref="PlotLogger"/>. The
/// CSV is the file the production exporter writes; Logged Data is the repository the pane reads.
/// </para>
/// <para>
/// On the app-host collection because <see cref="PlotLogger"/> creates its series through
/// <c>LoggingManager.Instance</c>.
/// </para>
/// </remarks>
[Collection(AppHostCollection.Name)]
public sealed class ChannelRenameDuringLoggingTests : IDisposable
{
    private const string Serial = "SN-668";
    private const string DeviceName = "Nq1";
    private const double Ai0Reading = 1.0;
    private const double Ai1Reading = 5.0;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "rename-" + Guid.NewGuid().ToString("N"));

    private readonly IDbContextFactory<LoggingContext> _contexts;
    private readonly LoggingManager _manager;
    private readonly DatabaseLogger _writer;
    private readonly PlotLogger _plot;
    private readonly BenchDevice _device;
    private readonly AnalogChannel _ai0;
    private readonly AnalogChannel _ai1;
    private long _ticks = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc).Ticks;

    public ChannelRenameDuringLoggingTests()
    {
        Daqifi.Desktop.App.InitializeMobile();

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "DAQiFiDatabase.db");
        _contexts = TestDatabase.Contexts(path);
        DatabaseMigrator.ApplyMigrations(_contexts, path);
        _manager = new LoggingManager(_contexts, Path.Combine(_directory, "profiles.xml"))
        {
            CurrentMode = LoggingMode.Stream
        };
        _writer = new DatabaseLogger(_contexts);
        typeof(DatabaseLogger)
            .GetField("_marshalToUiThread", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_writer, (Action<Action>)(action => action()));
        _plot = new PlotLogger();
        _manager.AddLogger(_plot);

        _device = new BenchDevice { Name = DeviceName, DeviceSerialNo = Serial };
        _ai0 = new AnalogChannel(_device, new CoreAnalogChannel(0)) { Name = "AI0" };
        _ai1 = new AnalogChannel(_device, new CoreAnalogChannel(1)) { Name = "AI1" };
        _device.DataChannels.Add(_ai0);
        _device.DataChannels.Add(_ai1);
    }

    public void Dispose()
    {
        _writer.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// The issue's reproduction: AI1 renamed to "Temp" one keystroke at a time while logging. The CSV
    /// must keep two data columns, and Logged Data and the live plot must each hold one continuous
    /// AI1 trace with every sample in it.
    /// </summary>
    [Fact]
    public async Task A_rename_during_logging_keeps_one_column_and_one_continuous_trace()
    {
        var session = await Record(() =>
        {
            Frames(20);
            foreach (var text in new[] { "", "T", "Te", "Tem", "Temp" })
            {
                _ai1.Name = text;
                Frames(5);
            }

            Frames(20);
        });
        const int frames = 65;

        var csv = Export(session);
        Assert.Equal(["AI0", "AI1"], csv.Channels);
        Assert.Equal(frames, csv.Rows.Count);
        Assert.All(csv.Rows, row => Assert.Equal(Ai1Reading, row[1]));

        var loggedData = new SessionDataRepository(_contexts, new RecordingAppLogger()).LoadInitialSession(session.ID);
        Assert.Equal(["AI0", "AI1"], loggedData.Channels.Select(c => c.ChannelName));
        Assert.Equal(frames, loggedData.Points[(Serial, "AI1")].Count);

        _plot.DrainPendingSamples();
        Assert.Equal(2, _plot.LoggedPoints.Count);
        Assert.Equal(frames, _plot.LoggedPoints[(Serial, "AI1")].Count);
        Assert.Equal("Temp", _ai1.Name);
    }

    /// <summary>
    /// The worse case from the issue's comment: on the way to "AI0 b", the box holds "AI0", the name
    /// of the other channel. No AI1 reading may land in AI0's column or on AI0's trace.
    /// </summary>
    [Fact]
    public async Task An_intermediate_name_equal_to_another_channel_does_not_put_readings_in_its_column()
    {
        var session = await Record(() =>
        {
            Frames(10);
            foreach (var text in new[] { "A", "AI", "AI0", "AI0 ", "AI0 b" })
            {
                _ai1.Name = text;
                Frames(4);
            }

            Frames(10);
        });

        // The corruption first: whatever else is wrong, AI0's column holds only AI0's readings.
        var csv = Export(session);
        var ai0Column = csv.Channels.ToList().IndexOf("AI0");
        Assert.All(csv.Rows, row => Assert.Equal(Ai0Reading, row[ai0Column]));
        _plot.DrainPendingSamples();
        Assert.All(_plot.LoggedPoints[(Serial, "AI0")], point => Assert.Equal(Ai0Reading, point.Y));

        Assert.Equal(["AI0", "AI1"], csv.Channels);
        Assert.All(csv.Rows, row => Assert.Equal(Ai1Reading, row[1]));
    }

    /// <summary>
    /// #670's data half: two channels the user gave one name, both logging. Their readings must stay in
    /// two columns, each named so the user can tell them apart, and each holding only its own input.
    /// </summary>
    [Fact]
    public async Task Two_channels_with_one_name_log_into_two_columns()
    {
        _ai1.Name = "AI0";

        var session = await Record(() => Frames(30));

        var csv = Export(session);
        Assert.Equal(["AI0", "AI0 (AI1)"], csv.Channels);
        Assert.Equal(30, csv.Rows.Count);
        Assert.All(csv.Rows, row => Assert.Equal(Ai0Reading, row[0]));
        Assert.All(csv.Rows, row => Assert.Equal(Ai1Reading, row[1]));

        _plot.DrainPendingSamples();
        Assert.Equal(2, _plot.LoggedPoints.Count);
        Assert.Equal("AI0", _ai1.Name);
    }

    /// <summary>
    /// The rename is not lost: the session that starts after it records under the new name. A rename
    /// while logging is off works exactly as before.
    /// </summary>
    [Fact]
    public async Task The_next_session_records_under_the_new_name()
    {
        await Record(() =>
        {
            Frames(5);
            _ai1.Name = "Temp";
            Frames(5);
        });

        var second = await Record(() => Frames(5));

        Assert.Equal(["AI0", "Temp"], Export(second).Channels);
    }

    /// <summary>A channel the user cleared to no name records under its input's own label.</summary>
    [Fact]
    public async Task A_channel_with_no_name_records_under_its_input_label()
    {
        _ai1.Name = "";

        var session = await Record(() => Frames(5));

        Assert.Equal(["AI0", "AI1"], Export(session).Channels);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>A wrapper with no transport. Nothing here reads what it would send.</summary>
    private sealed class BenchDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message)
        {
        }
    }

    /// <summary>
    /// One logging session over AI0 and AI1, started and stopped the way the phone's recorder does it,
    /// which waits until the writer has committed every sample.
    /// </summary>
    private async Task<LoggingSession> Record(Action duringSession)
    {
        var recorder = new LoggingManagerStreamRecorder(
            () => _manager, () => _contexts, _ => _writer, TimeSpan.FromSeconds(30));
        Assert.Null(recorder.Start([_ai0, _ai1]));
        var session = _manager.Session;

        duringSession();

        var result = await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(result.IsSaved, result.Problem);
        return session;
    }

    /// <summary>
    /// <paramref name="count"/> stream frames at 100 Hz, each one sample of AI0 and one of AI1 at the
    /// same timestamp, through the production publish path.
    /// </summary>
    private void Frames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _ticks += TimeSpan.TicksPerMillisecond * 10;
            var timestamp = new DateTime(_ticks, DateTimeKind.Utc);
            _ai0.ActiveSample = new DataSample(_device, _ai0, timestamp, Ai0Reading);
            _ai1.ActiveSample = new DataSample(_device, _ai1, timestamp, Ai1Reading);
        }
    }

    private sealed record Csv(IReadOnlyList<string> Channels, IReadOnlyList<double?[]> Rows);

    /// <summary>
    /// Exports <paramref name="session"/> with the production exporter and reads the file back: the
    /// channel name of each data column, and each row's cells (null where the cell is empty).
    /// </summary>
    private Csv Export(LoggingSession session)
    {
        var path = Path.Combine(_directory, $"session-{session.ID}-{Guid.NewGuid():N}.csv");
        Assert.True(new OptimizedLoggingSessionExporter(_contexts).ExportLoggingSession(
            session, path, exportRelativeTime: false, new Progress<int>(), CancellationToken.None, 0, 1));

        // No name used here contains a comma or a quote, so a split is a full parse once a field the
        // writer quoted (a name with a trailing space, for example) is unquoted.
        var lines = File.ReadAllLines(path).Where(line => line.Length > 0).ToList();
        var prefix = $"{DeviceName}:{Serial}:";
        var channels = lines[0].Split(',').Skip(1)
            .Select(column =>
            {
                var unquoted = column.Length >= 2 && column[0] == '"' && column[^1] == '"' ? column[1..^1] : column;
                Assert.StartsWith(prefix, unquoted, StringComparison.Ordinal);
                return unquoted[prefix.Length..];
            })
            .ToList();
        var rows = lines.Skip(1)
            .Select(line => line.Split(',').Skip(1)
                .Select(cell => cell.Length == 0
                    ? (double?)null
                    : double.Parse(cell, NumberStyles.Float, CultureInfo.InvariantCulture))
                .ToArray())
            .ToList();
        return new Csv(channels, rows);
    }
}
