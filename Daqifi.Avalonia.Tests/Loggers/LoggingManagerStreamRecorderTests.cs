using System.Reflection;
using Daqifi.Avalonia.Views;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// PR #609 review: the phone's recorder reported a session as saved as soon as it entered the
/// in-memory list, before the background writer had committed its samples. APP LOGS lists only a
/// session with a committed sample, so the user could open it and find nothing. These tests drive
/// the REAL <see cref="LoggingManagerStreamRecorder"/> over a real <see cref="LoggingManager"/>, a
/// real <see cref="DatabaseLogger"/> and a migrated SQLite file, with the writer paused.
/// </summary>
/// <remarks>
/// The logger's UI marshal is replaced with a direct call, as in
/// <c>DatabaseLoggerSessionOpenTests</c>: no dispatcher runs in this project.
/// </remarks>
public sealed class LoggingManagerStreamRecorderTests : IDisposable
{
    private const string Serial = "SERIAL-REC";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "recorder-" + Guid.NewGuid().ToString("N"));

    private readonly IDbContextFactory<LoggingContext> _contexts;
    private readonly LoggingManager _manager;
    private readonly DatabaseLogger _writer;
    private readonly TestChannel _channel = new("AI0", Serial);

    public LoggingManagerStreamRecorderTests()
    {
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
    }

    public void Dispose()
    {
        _writer.ResumeConsumer();
        _writer.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private LoggingManagerStreamRecorder Recorder(TimeSpan commitWait) =>
        new(() => _manager, () => _contexts, _ => _writer, commitWait);

    private void Sample(double value) =>
        _manager.HandleChannelUpdate(_channel, new DataSample
        {
            Value = value,
            TimestampTicks = DateTime.UtcNow.Ticks,
            DeviceName = "Nq1",
            ChannelName = _channel.Name,
            DeviceSerialNo = Serial,
            Color = "#FF0000",
            Type = ChannelType.Analog,
        });

    [Fact]
    public async Task Stop_is_not_confirmed_until_the_paused_writer_commits_the_samples()
    {
        var recorder = Recorder(TimeSpan.FromSeconds(20));
        Assert.Null(recorder.Start([_channel]));
        _writer.SuspendConsumer();
        Sample(1.5);

        var stop = recorder.StopAsync();
        Assert.False(_manager.Active, "The session must close at once, on the calling thread.");
        await Task.Delay(500);
        Assert.False(stop.IsCompleted, "The stop was confirmed while the sample was not yet in SQLite.");

        _writer.ResumeConsumer();
        var result = await stop.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(result.IsSaved);
        Assert.Equal(1, result.CommittedSamples);
        Assert.Equal(_manager.Session.Name, result.SessionName);
        await using var context = await _contexts.CreateDbContextAsync();
        Assert.Equal(1, await context.Samples.CountAsync(s => s.LoggingSessionID == _manager.Session.ID));
    }

    [Fact]
    public async Task A_writer_that_does_not_finish_in_time_is_reported_and_never_called_saved()
    {
        var recorder = Recorder(TimeSpan.FromMilliseconds(400));
        Assert.Null(recorder.Start([_channel]));
        _writer.SuspendConsumer();
        Sample(2.5);

        var result = await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(result.IsSaved);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public async Task A_recording_with_no_sample_is_not_saved_and_has_no_problem()
    {
        var recorder = Recorder(TimeSpan.FromSeconds(5));
        Assert.Null(recorder.Start([_channel]));

        var result = await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(result.IsSaved);
        Assert.Null(result.Problem);
        Assert.Equal(0, result.CommittedSamples);
    }

    [Fact]
    public async Task Stop_with_no_open_session_completes_at_once()
    {
        var result = await Recorder(TimeSpan.FromSeconds(5)).StopAsync();

        Assert.Null(result.SessionName);
        Assert.False(result.IsSaved);
    }

    private sealed class TestChannel : AbstractChannel
    {
        private string _name;

        public TestChannel(string name, string serial)
        {
            _name = name;
            DeviceSerialNo = serial;
        }

        public override string Name
        {
            get => _name;
            set => _name = value;
        }

        public override ChannelDirection Direction { get; set; } = ChannelDirection.Input;
        public override int Index => 0;
        public override ChannelType Type => ChannelType.Analog;
        public override bool IsActive { get; set; }
        public override bool IsDigital => false;
        public override bool IsAnalog => true;
    }
}
