using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using ChannelType = Daqifi.Core.Channel.ChannelType;

// The live-plot load checks (issue #595). Two halves, kept out of Program.cs so the rows other PRs
// edit there do not collide with this one:
//
//   * --all-ai on a board run: every analog input on, the rate at the device's cap, and a
//     STREAM-LOAD/limits row that reports sample loss from the device's own timestamps, the
//     UI-pump maximum during the stream, and what one live-plot redraw costs.
//   * --scripted plot-ingest: no board. A producer thread feeds 16 channels at 3518 Hz straight
//     into the live plot's logger while the UI thread pumps, then holds the plot lock for 2 s on
//     purpose. It measures how long the producer — standing in for the device transport thread —
//     was blocked. Before #595 that was the whole 2 s: the reader waited on the redraw.

internal static partial class HeadlessBench
{
    private const string StatePlotIngest = "plot-ingest";

    /// <summary>Per pump iteration, the dispatcher drain plus the render tick that follows it — so,
    /// unlike <see cref="PumpLatenciesMs"/>, it includes layout and the plot's draw. Index-aligned
    /// with <see cref="PumpLatenciesMs"/>.</summary>
    private static readonly List<double> FrameLatenciesMs = [];

    /// <summary>Set by <c>--all-ai</c>: turn on every analog input, not only the first one.</summary>
    private static bool _allAi;


    /// <summary>Channels and rate <see cref="StatePlotIngest"/> feeds: the bench Nq1's 16 analog
    /// inputs at the 3518 Hz cap it publishes for that channel set — the load #595 was found at.</summary>
    private const int IngestChannels = 16;
    private const int IngestRateHz = 3518;

    /// <summary>How long <see cref="StatePlotIngest"/> holds the plot lock on the UI thread.</summary>
    private static readonly TimeSpan IngestLockHold = TimeSpan.FromSeconds(2);

    /// <summary>The longest a single <c>PlotLogger.Log</c> call may take before the row fails. The
    /// device firmware drops samples once its buffer fills, so a transport thread that waits on the
    /// UI is data loss; 50 ms leaves room for a GC pause on a loaded machine.</summary>
    private const double MaxIngestBlockMs = 50;

    /// <summary>Device-timestamp deltas between consecutive samples of one channel. Filled under
    /// the STREAM-AI counter's own lock, read after that counter is unsubscribed.</summary>
    private sealed class DeviceGapTally
    {
        public readonly List<long> Deltas = [];
        public void Note(long deltaTicks) => Deltas.Add(deltaTicks);
    }

    private readonly record struct RedrawCost(int Count, double MedianMs, double MaxMs, int Points);

    /// <summary>
    /// Max, p99 and count over 100 ms of the UI frames recorded between two
    /// <see cref="FrameLatenciesMs"/> positions. A frame is one dispatcher drain plus one render
    /// tick: Avalonia lays out — and OxyPlot redraws — on the render tick, so the dispatcher drain
    /// alone (<see cref="PumpLatenciesMs"/>) never sees the plot's draw.
    /// </summary>
    private static (double Max, double P99, int Over100, int Count) FrameStats(int from, int to, List<double>? source = null)
    {
        var frames = (source ?? FrameLatenciesMs).Skip(from).Take(to - from).OrderBy(l => l).ToList();
        if (frames.Count == 0) { return (double.NaN, double.NaN, 0, 0); }
        var p99 = frames[(int)Math.Min(frames.Count - 1, Math.Floor(frames.Count * 0.99))];
        return (frames[^1], p99, frames.Count(l => l > 100), frames.Count);
    }

    /// <summary>The three slowest frames between two positions, each with its approximate offset into
    /// the window (frames are evenly spread over <paramref name="seconds"/>), so a one-off first-draw
    /// cost can be told from a stall that recurs.</summary>
    private static string SlowestFrames(int from, int to, double seconds)
    {
        var count = to - from;
        if (count <= 0) { return "none"; }
        return string.Join(", ", Enumerable.Range(from, count)
            .OrderByDescending(i => FrameLatenciesMs[i]).Take(3)
            .Select(i => $"{FrameLatenciesMs[i]:F0} ms at {(i - from) * seconds / count:F1} s"));
    }

    /// <summary>Set by <c>--hold-ui N</c>: halfway through the stream, the UI thread holds the live
    /// plot's lock for N seconds, the long UI job of #595. The firmware gives up on a host that has
    /// not read for 10 s, so N over 10 is the case that lost samples before the fix.</summary>
    private static int _holdUiSeconds;

    /// <summary>Set by <c>--hold-reader N</c>: halfway through the stream, the device reader thread
    /// is held for N seconds, once. That thread is Core's consumer, which also reads the port, so
    /// the firmware drops what its buffer cannot keep and the stream has a real gap. Since #600 the
    /// UI hold above can no longer cause one. STREAM-GAP then checks that the user is told (#595).</summary>
    private static int _holdReaderSeconds;

    /// <summary>STREAM-AI's window: <c>--seconds</c> of pumping, with the <c>--hold-ui</c> or
    /// <c>--hold-reader</c> hold in the middle of it when one was asked for.</summary>
    private static void PumpStreamWindow(DaqifiViewModel shell, IChannel ai)
    {
        var holdUi = _holdUiSeconds > 0 && shell.Plotter is not null;
        if (!holdUi && _holdReaderSeconds <= 0)
        {
            PumpFor(TimeSpan.FromSeconds(_seconds));
            return;
        }

        PumpFor(TimeSpan.FromSeconds(_seconds / 2.0));
        if (holdUi)
        {
            Console.WriteLine($"[INFO] holding the live plot's lock on the UI thread for {_holdUiSeconds} s");
            lock (shell.Plotter!.PlotModel.SyncRoot)
            {
                Thread.Sleep(TimeSpan.FromSeconds(_holdUiSeconds));
            }
        }
        if (_holdReaderSeconds > 0) { HoldReader(ai); }
        PumpFor(TimeSpan.FromSeconds(_seconds / 2.0));
    }

    /// <summary>Holds the device reader thread for <see cref="_holdReaderSeconds"/> inside one
    /// sample callback of <paramref name="ai"/>, which Core raises on that thread. The UI goes on
    /// pumping, so the hold is the reader's alone. Returns when the reader is free again.</summary>
    private static void HoldReader(IChannel ai)
    {
        var state = 0; // 0 armed, 1 holding, 2 released
        OnChannelUpdatedHandler hold = (_, _) =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) { return; }
            Thread.Sleep(TimeSpan.FromSeconds(_holdReaderSeconds));
            Volatile.Write(ref state, 2);
        };
        ai.OnChannelUpdated += hold;
        Console.WriteLine($"[INFO] holding the device reader thread for {_holdReaderSeconds} s");
        PumpUntil(() => Volatile.Read(ref state) == 2, TimeSpan.FromSeconds(_holdReaderSeconds + 10));
        ai.OnChannelUpdated -= hold;
    }

    /// <summary>
    /// STREAM-GAP/works: the user is told about a gap in the stream, once, and only when there is
    /// one (#595). Core finds the gap; the bell must hold exactly one notice for this device when
    /// Core counted any gap, and none when it counted none. With <c>--hold-reader</c>, the run must
    /// also have a gap. The rig's own device-timestamp tally is reported beside Core's count.
    /// </summary>
    private static void ReportStreamGap(Window main, DaqifiViewModel shell, IStreamingDevice device,
        DeviceGapTally gaps, int commandedRate, string? shot)
    {
        var coreGaps = (device as AbstractStreamingDevice)?.StreamGapCount ?? -1;
        var coreGapSeconds = (device as AbstractStreamingDevice)?.StreamGapTime.TotalSeconds ?? double.NaN;
        var notices = shell.NotificationList
            .Where(n => n.DeviceSerialNo == device.DeviceSerialNo && n.Message.Contains(": samples are missing. "))
            .Select(n => n.Message)
            .ToList();
        double periodTicks = commandedRate > 0 ? TimeSpan.TicksPerSecond / (double)commandedRate : double.NaN;
        var rigGapDeltas = gaps.Deltas.Where(d => d > 2 * periodTicks).ToList();
        var rigGaps = rigGapDeltas.Count;
        var rigGapSeconds = rigGapDeltas.Sum(d => (double)d) / TimeSpan.TicksPerSecond;
        var pass = coreGaps >= 0
                   && notices.Count == (coreGaps > 0 ? 1 : 0)
                   && (_holdReaderSeconds <= 0 || coreGaps > 0);

        // What the user sees: the bell's panel, open, with the notice in it.
        if (notices.Count > 0)
        {
            shell.IsNotificationsOpen = true;
            PumpFor(TimeSpan.FromMilliseconds(600));
            shot = Capture(main, "t2-07b-gap-notice");
            shell.IsNotificationsOpen = false;
            Pump();
        }

        Step(2, "STREAM-GAP", "works", pass,
             $"{(_holdReaderSeconds > 0 ? $"reader held for {_holdReaderSeconds} s; " : "no hold; ")}" +
             $"Core found {coreGaps} gaps, {coreGapSeconds:F3} s in all (the rig's own device-timestamp tally: " +
             $"{rigGaps} over 2 periods, {rigGapSeconds:F3} s); " +
             $"{notices.Count} gap notices in the bell for this device" +
             (notices.Count > 0 ? $": \"{notices[0]}\"" : ""),
             shot);
    }

    /// <summary>The rest of the analog inputs, through the tile command a user clicks.</summary>
    private static void EnableEveryOtherAnalogInput(ChannelsPaneViewModel pane, IChannel first)
    {
        foreach (var tile in pane.AnalogInputs.ToList())
        {
            if (ReferenceEquals(tile.Channel, first) || tile.IsActive) { continue; }
            pane.ToggleChannelCommand.Execute(tile);
            Pump();
        }
    }

    /// <summary>Turns every analog input off again, so the shared bench board is left with no
    /// channel enabled.</summary>
    private static void DisableEveryAnalogInput(ChannelsPaneViewModel pane)
    {
        foreach (var tile in pane.AnalogInputs.ToList())
        {
            if (!tile.IsActive) { continue; }
            pane.ToggleChannelCommand.Execute(tile);
            Pump();
        }
    }

    /// <summary>
    /// What one full live-plot redraw costs on the UI thread: the model update plus OxyPlot's render
    /// into its canvas, forced through the view's own <c>InvalidatePlot</c> and a layout pass. Taken
    /// while the device is still streaming, so the ingest contends with it exactly as in use.
    /// </summary>
    private static RedrawCost MeasureLiveRedraw(Window main, int count = 15)
    {
        var view = main.GetVisualDescendants().OfType<OxyPlot.Avalonia.PlotView>()
            .FirstOrDefault(v => v.Name == "DataLog");
        if (view is null || !view.IsEffectivelyVisible || view.ActualModel is null)
        {
            return default;
        }

        var times = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            view.InvalidatePlot(true);
            view.UpdateLayout();
            times.Add(sw.Elapsed.TotalMilliseconds);
            Pump();
        }

        times.Sort();
        var points = view.ActualModel.Series.OfType<OxyPlot.Series.DataPointSeries>()
            .Sum(s => s.ItemsSource is System.Collections.ICollection c ? c.Count : 0);
        return new RedrawCost(count, times[count / 2], times[^1], points);
    }

    /// <summary>
    /// STREAM-LOAD/limits, for <c>--all-ai</c> runs: was every sample delivered while all analog
    /// inputs streamed at the cap, and did the UI stay responsive? Loss is judged by the device's
    /// own timestamps on the counted channel, so it measures samples the board took and the app
    /// never saw — the firmware drops them when the host stops reading.
    /// </summary>
    private static void ReportStreamLoad(IStreamingDevice device, IChannel ai, bool started, int counted,
        double window, int commandedRate, double deviceSpan, DeviceGapTally gaps, int pumpsFrom, int pumpsTo,
        RedrawCost redraw, LoggingSession? session, string? shot)
    {
        if (!_allAi) { return; }

        var activeAi = device.DataChannels.Count(c => c.IsAnalog && c.IsActive && c.Direction == ChannelDirection.Input);
        // Both spans run from the first to the last counted sample (STREAM-AI measures them that
        // way), so a span of S seconds at R Hz should hold S * R intervals: S * R + 1 samples.
        var expectedByDevice = double.IsNaN(deviceSpan) ? double.NaN : deviceSpan * commandedRate + 1;
        var lossByDevice = expectedByDevice > 0 ? 1 - counted / expectedByDevice : double.NaN;
        var lossByWall = window > 0 && commandedRate > 0 ? 1 - counted / (window * commandedRate + 1) : double.NaN;

        double periodTicks = commandedRate > 0 ? TimeSpan.TicksPerSecond / (double)commandedRate : double.NaN;
        long gapCount = 0, maxGapTicks = 0;
        double missingTicks = 0;
        // No lock: the counter that fills this was unsubscribed when the stream stopped.
        foreach (var d in gaps.Deltas)
        {
            if (d > maxGapTicks) { maxGapTicks = d; }
            if (d > 2 * periodTicks)
            {
                gapCount++;
                missingTicks += d - periodTicks;
            }
        }

        var (pumpMax, pumpP99, pumpsOver100, pumpCount) = FrameStats(pumpsFrom, pumpsTo);
        var jobsMax = FrameStats(pumpsFrom, pumpsTo, PumpLatenciesMs).Max;

        var perChannelInSession = session?.SampleCount is { } n && activeAi > 0 ? n / activeAi : (long?)null;
        var missingSeconds = missingTicks / TimeSpan.TicksPerSecond;
        var pass = started && activeAi > 1 && lossByDevice <= 0.001 && missingSeconds <= deviceSpan * 0.001;

        Step(2, "STREAM-LOAD", "limits", pass,
             (
                 $"{activeAi} analog inputs at {commandedRate} Hz for {window:F1} s; '{ai.Name}' got {counted} samples " +
                 $"against {expectedByDevice:F0} by the device clock -> loss {lossByDevice:P3} " +
                 $"(by wall clock {lossByWall:P3}); {gapCount} device-timestamp gaps over 2 periods, " +
                 $"{missingSeconds:F3} s missing, largest {maxGapTicks / 10_000.0:F2} ms; " +
                 $"session holds {perChannelInSession?.ToString(CultureInfo.InvariantCulture) ?? "?"} per channel; " +
                 $"UI frame during the stream: max {pumpMax:F0} ms, p99 {pumpP99:F1} ms, {pumpsOver100} of {pumpCount} over 100 ms " +
                 $"(dispatcher jobs alone: max {jobsMax:F0} ms; slowest {SlowestFrames(pumpsFrom, pumpsTo, window)}); " +
                 $"live redraw: median {redraw.MedianMs:F1} ms, max {redraw.MaxMs:F1} ms over {redraw.Count} " +
                 $"for {redraw.Points} buffered points"),
             shot, window);
    }

    /// <summary>
    /// GRAPH-INGEST/limits (no board): the live plot's ingest must never wait for the UI. A producer
    /// thread stands in for the device transport thread and calls <c>PlotLogger.Log</c> for 16
    /// channels at 3518 Hz, on a schedule, as a board pushes frames. The UI thread pumps as it does in
    /// use, and then holds <c>PlotModel.SyncRoot</c> for <see cref="IngestLockHold"/> — a long redraw.
    /// The row measures the longest single <c>Log</c> call and how far the producer fell behind its
    /// schedule; on a board, that lag is what overflows the device's buffer.
    /// </summary>
    private static void RunPlotIngestRow(Window main, DaqifiViewModel shell)
    {
        shell.SelectedIndex = LiveGraphTabIndex;
        PumpFor(TimeSpan.FromMilliseconds(500));

        var plotter = shell.Plotter;
        var names = Enumerable.Range(0, IngestChannels).Select(i => $"AI{i}").ToArray();
        var stop = 0;
        long produced = 0, maxLogTicks = 0, logsOver10Ms = 0, maxLagFrames = 0;
        // 0 before the deliberate hold, 1 during it, 2 after: a Log() call that starts or ends in a
        // different phase than it began in overlapped the hold.
        var holdPhase = 0;
        long maxLogTicksWhileHeld = 0;
        Exception? producerFault = null;

        var producer = new Thread(() =>
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var baseTicks = DateTime.UtcNow.Ticks;
                long frame = 0;
                while (Volatile.Read(ref stop) == 0)
                {
                    var due = (long)(clock.Elapsed.TotalSeconds * IngestRateHz);
                    if (frame >= due) { Thread.Sleep(1); continue; }
                    if (due - frame > maxLagFrames) { maxLagFrames = due - frame; }
                    for (; frame < due && Volatile.Read(ref stop) == 0; frame++)
                    {
                        var ticks = baseTicks + frame * TimeSpan.TicksPerSecond / IngestRateHz;
                        for (var c = 0; c < IngestChannels; c++)
                        {
                            var sample = new DataSample
                            {
                                ChannelName = names[c],
                                DeviceName = "Scripted Nyquist",
                                DeviceSerialNo = ScriptedSerial,
                                Type = ChannelType.Analog,
                                Color = "#FF4A9EFF",
                                Value = Math.Sin((frame + c * 37) / 200.0),
                                TimestampTicks = ticks,
                            };
                            var phaseBefore = Volatile.Read(ref holdPhase);
                            var t0 = Stopwatch.GetTimestamp();
                            plotter.Log(sample);
                            var dt = Stopwatch.GetTimestamp() - t0;
                            var overlapped = phaseBefore == 1 || Volatile.Read(ref holdPhase) != phaseBefore;
                            if (overlapped) { if (dt > maxLogTicksWhileHeld) { maxLogTicksWhileHeld = dt; } }
                            else if (dt > maxLogTicks) { maxLogTicks = dt; }
                            if (dt * 1000.0 / Stopwatch.Frequency > 10) { logsOver10Ms++; }
                            produced++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                producerFault = ex;
            }
        })
        { IsBackground = true, Name = "plot-ingest producer" };

        // How many model updates (one per redraw) the UI thread managed while the producer ran: a
        // plot that stops redrawing under load would otherwise read as a fast UI.
        var updates = 0;
        EventHandler countUpdate = (_, _) => updates++;
        var statsTicks = 0;
        System.ComponentModel.PropertyChangedEventHandler countStats = (_, e) =>
        {
            if (e.PropertyName == nameof(PlotLogger.PlotStatsSummary)) { statsTicks++; }
        };
        plotter.PropertyChanged += countStats;
#pragma warning disable CS0618 // Updated is obsolete in OxyPlot 2.2 but still raised once per update.
        plotter.PlotModel.Updated += countUpdate;
#pragma warning restore CS0618

        var pumpsFrom = FrameLatenciesMs.Count;
        var wall = Stopwatch.StartNew();
        producer.Start();
        PumpFor(TimeSpan.FromSeconds(Math.Max(3, _seconds)));
        var pumpsTo = FrameLatenciesMs.Count;
        var streamSeconds = wall.Elapsed.TotalSeconds;
        plotter.PropertyChanged -= countStats;
#pragma warning disable CS0618
        plotter.PlotModel.Updated -= countUpdate;
#pragma warning restore CS0618
        var redraw = MeasureLiveRedraw(main);

        // The long UI job, on purpose: the redraw's lock, held on the UI thread.
        lock (plotter.PlotModel.SyncRoot)
        {
            Volatile.Write(ref holdPhase, 1);
            Thread.Sleep(IngestLockHold);
            Volatile.Write(ref holdPhase, 2);
        }
        PumpFor(TimeSpan.FromSeconds(1));

        Volatile.Write(ref stop, 1);
        producer.Join(TimeSpan.FromSeconds(10));
        wall.Stop();
        PumpFor(TimeSpan.FromMilliseconds(300));

        var shot = Capture(main, "t1-plot-ingest");
        var series = plotter.LoggedChannels.Count;
        var points = plotter.LoggedPoints.Values.Sum(l => l.Count);
        var (pumpMax, pumpP99, pumpsOver100, pumpCount) = FrameStats(pumpsFrom, pumpsTo);
        var jobsMax = FrameStats(pumpsFrom, pumpsTo, PumpLatenciesMs).Max;
        var maxLogMs = maxLogTicks * 1000.0 / Stopwatch.Frequency;
        var maxLogHeldMs = maxLogTicksWhileHeld * 1000.0 / Stopwatch.Frequency;
        var maxLagMs = maxLagFrames * 1000.0 / IngestRateHz;
        var expected = wall.Elapsed.TotalSeconds * IngestRateHz * IngestChannels;

        Step(1, "GRAPH-INGEST", "limits",
             producerFault is null && maxLogHeldMs < MaxIngestBlockMs && series == IngestChannels && points > 0,
             (
                 $"{produced} samples fed ({produced / expected:P1} of {IngestChannels} ch x {IngestRateHz} Hz over {wall.Elapsed.TotalSeconds:F1} s); " +
                 $"longest Log() outside the hold {maxLogMs:F1} ms, {logsOver10Ms} calls over 10 ms in all; longest Log() while the UI held the plot lock " +
                 $"for {IngestLockHold.TotalSeconds:F0} s: {maxLogHeldMs:F1} ms; producer fell up to {maxLagMs:F0} ms behind schedule; " +
                 $"{updates / streamSeconds:F1} plot updates and {statsTicks / streamSeconds:F1} stats refreshes a second while streaming; " +
                 $"UI frame while streaming: max {pumpMax:F0} ms, p99 {pumpP99:F1} ms, {pumpsOver100} of {pumpCount} over 100 ms " +
                 $"(dispatcher jobs alone: max {jobsMax:F0} ms; slowest {SlowestFrames(pumpsFrom, pumpsTo, streamSeconds)}); " +
                 $"live redraw: median {redraw.MedianMs:F1} ms, max {redraw.MaxMs:F1} ms for {redraw.Points} buffered points; " +
                 $"plot shows {series} series, {points} points" +
                 (producerFault is null ? "" : $"; producer threw {producerFault.GetType().Name}: {producerFault.Message}")),
             shot, wall.Elapsed.TotalSeconds);
    }
}
