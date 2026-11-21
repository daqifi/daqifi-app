using System.Reflection;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Models;
using Microsoft.EntityFrameworkCore;
using OxyPlot.Series;
using Xunit;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// The property under test: every channel that has samples stored in a session gets a trace in
/// Logged Data. Each row records a session through the live path (channel scaling, then
/// <see cref="LoggingManager"/>, then the real <see cref="DatabaseLogger"/> writing a migrated
/// SQLite file), then opens it through the two readers the panes use:
/// <see cref="DatabaseLogger.DisplayLoggingSession"/> (desktop) and
/// <see cref="SessionDataRepository.LoadInitialSession"/> (the mobile viewer).
/// </summary>
public sealed class LoggedDataChannelDiscoveryTests : IDisposable
{
    private static readonly long Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>10 ms between frames: a 100 Hz stream.</summary>
    private const long FramePeriodTicks = 100_000;

    private const int Frames = 50;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "discovery-" + Guid.NewGuid().ToString("N"));

    public LoggedDataChannelDiscoveryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>One channel of one row: who owns it, when its first frame arrives, and what it reads.</summary>
    public sealed record ChannelSpec(
        string Serial, string Name, long FirstFrameOffsetTicks, string? Scale, double FirstRaw, double LaterRaw);

    public static TheoryData<string, ChannelSpec[]> Rows => new()
    {
        {
            "R1 one device, two channels, both in every frame",
            [new("S1", "AI0", 0, null, 1, 1), new("S1", "AI1", 0, null, 2, 2)]
        },
        {
            "R2 two devices, first frames at the same tick",
            [new("S1", "AI0", 0, null, 1, 1), new("S2", "AI0", 0, null, 2, 2)]
        },
        {
            "R3 two devices, the second device's first frame 3 ms later",
            [new("S1", "AI0", 0, null, 1, 1), new("S2", "AI0", 30_000, null, 2, 2)]
        },
        {
            "R4 two devices, the second device's first frame 3 ms earlier",
            [new("S1", "AI0", 30_000, null, 1, 1), new("S2", "AI0", 0, null, 2, 2)]
        },
        {
            "R5 one device, AI1 scaled Sqrt(x), first reading positive",
            [new("S1", "AI0", 0, null, 1, 1), new("S1", "AI1", 0, "Sqrt(x)", 4, 4)]
        },
        {
            "R6 one device, AI1 scaled Sqrt(x), first reading -0.01 (refused), then 4",
            [new("S1", "AI0", 0, null, 1, 1), new("S1", "AI1", 0, "Sqrt(x)", -0.01, 4)]
        },
        {
            "R7 one device, AI1 scaled 1 / x, first reading 0 (refused), then 2",
            [new("S1", "AI0", 0, null, 1, 1), new("S1", "AI1", 0, "1 / x", 0, 2)]
        },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_channel_with_stored_samples_gets_a_trace(string row, ChannelSpec[] specs)
    {
        var path = Path.Combine(_directory, "DAQiFiDatabase.db");
        var contexts = TestDatabase.Contexts(path);
        DatabaseMigrator.ApplyMigrations(contexts, path);

        var manager = new LoggingManager(contexts, Path.Combine(_directory, "profiles.xml"))
        {
            CurrentMode = LoggingMode.Stream
        };
        using var logger = new DatabaseLogger(contexts);
        typeof(DatabaseLogger)
            .GetField("_marshalToUiThread", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(logger, (Action<Action>)(action => action()));
        manager.AddLogger(logger);

        var channels = specs.Select(s =>
        {
            var channel = new TestChannel(s.Name, s.Serial);
            if (s.Scale != null)
            {
                channel.ScaleExpression = s.Scale;
                channel.IsScalingActive = true;
            }
            manager.Subscribe(channel);
            return (Spec: s, Channel: channel);
        }).ToList();

        manager.Active = true;
        Assert.Null(manager.SessionStartFailure);
        var session = manager.Session;

        // Publish in arrival order, as the device threads would.
        var frames = channels
            .SelectMany(c => Enumerable.Range(0, Frames).Select(k => (c.Spec, c.Channel, Frame: k,
                Ticks: Start + c.Spec.FirstFrameOffsetTicks + k * FramePeriodTicks)))
            .OrderBy(f => f.Ticks)
            .ToList();
        foreach (var f in frames)
        {
            f.Channel.ActiveSample = new DataSample
            {
                DeviceName = "Nq1",
                DeviceSerialNo = f.Spec.Serial,
                ChannelName = f.Spec.Name,
                Type = ChannelType.Analog,
                Color = "#FFFF0000",
                Value = f.Frame == 0 ? f.Spec.FirstRaw : f.Spec.LaterRaw,
                TimestampTicks = f.Ticks,
            };
        }

        logger.WaitForIdle(TimeSpan.FromSeconds(20));
        Assert.False(logger.HasUnsavedSamples, $"{row}: the writer did not commit in time");

        var expected = specs.Select(s => (s.Serial, s.Name)).OrderBy(k => k).ToList();

        // Premise: every channel's data is in the database.
        using (var context = contexts.CreateDbContext())
        {
            foreach (var (serial, name) in expected)
            {
                var stored = context.Samples.Count(x =>
                    x.LoggingSessionID == session.ID && x.DeviceSerialNo == serial && x.ChannelName == name);
                Assert.True(stored >= Frames - 1, $"{row}: {serial}/{name} has only {stored} stored samples");
            }
        }

        // Consequence, mobile reader.
        var discovered = new SessionDataRepository(contexts, new RecordingAppLogger())
            .LoadInitialSession(session.ID).Channels
            .Select(c => (c.DeviceSerialNo, c.ChannelName)).OrderBy(k => k).ToList();

        // Consequence, desktop Logged Data pane.
        logger.DisplayLoggingSession(session);
        var traces = logger.PlotModel.Series.OfType<LineSeries>()
            .Select(s => s.Tag is (string serial, string name) ? (serial, name) : ("?", "?"))
            .OrderBy(k => k).ToList();

        Assert.True(expected.SequenceEqual(discovered),
            $"{row}: stored {Describe(expected)}, mobile viewer found {Describe(discovered)}");
        Assert.True(expected.SequenceEqual(traces),
            $"{row}: stored {Describe(expected)}, Logged Data drew {Describe(traces)}");
    }

    private static string Describe(IEnumerable<(string, string)> keys) =>
        "[" + string.Join(", ", keys.Select(k => $"{k.Item1}/{k.Item2}")) + "]";

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
