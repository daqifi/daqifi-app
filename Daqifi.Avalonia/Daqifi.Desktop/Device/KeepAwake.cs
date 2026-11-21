using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Daqifi.Desktop.Common.Loggers;

namespace Daqifi.Desktop.Device;

/// <summary>
/// Keeps the computer awake while a device streams to the app.
/// </summary>
/// <remarks>
/// A device calls <see cref="Acquire"/> one time when its stream starts and <see cref="Release"/>
/// one time when that stream ends. The implementation counts the streams.
/// </remarks>
internal interface IKeepAwake
{
    /// <summary>One more stream needs the computer awake.</summary>
    void Acquire();

    /// <summary>One stream no longer needs the computer awake.</summary>
    void Release();
}

/// <summary>
/// The <see cref="IKeepAwake"/> the app uses: one thread owns the Windows execution state and
/// counts the streams that need it (issue #696).
/// </summary>
/// <remarks>
/// <para>
/// <c>SetThreadExecutionState</c> sets the state of the thread that calls it, and a thread has
/// one state. When each device called it directly, that caused two defects:
/// </para>
/// <list type="number">
/// <item>A release must run on the thread that set the state. A stream starts on the UI thread,
/// but Core reports a drop on its transport thread, so a release there clears the state of the
/// wrong thread.</item>
/// <item>The state is not counted. With two devices streaming, the stop of one device cleared
/// the state that the other device still needed.</item>
/// </list>
/// <para>
/// So every request goes to one background thread, in order. That thread counts the streams. It
/// sets the state when the count goes from 0 to 1, and it clears the state when the count goes
/// from 1 to 0. A caller on any thread only queues its request, so a caller on Core's transport
/// thread never waits for the operating system.
/// </para>
/// <para>
/// The thread starts with the first request and lives as long as the process. It is a
/// background thread, so it does not keep the process alive.
/// </para>
/// </remarks>
internal sealed class KeepAwakeOwner : IKeepAwake, IDisposable
{
    private readonly Action<bool> _apply;
    private readonly BlockingCollection<bool> _requests = new();
    private readonly Lazy<Thread> _thread;

    /// <param name="apply">
    /// Sets the state: <c>true</c> keeps the computer awake, <c>false</c> lets it sleep. Only the
    /// owner's thread calls it.
    /// </param>
    internal KeepAwakeOwner(Action<bool> apply)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _thread = new Lazy<Thread>(StartThread);
    }

    /// <summary>
    /// The one the app uses. On Windows it sets the execution state; on the other heads it does
    /// nothing (a mobile wake lock is a future divergence, not required to stream).
    /// </summary>
    public static IKeepAwake ForThisProcess { get; } =
        OperatingSystem.IsWindows() ? new KeepAwakeOwner(SetWindowsExecutionState) : new NoKeepAwake();

    public void Acquire() => Request(acquire: true);

    public void Release() => Request(acquire: false);

    /// <summary>Stops the owner's thread after the requests already queued. For tests.</summary>
    public void Dispose() => _requests.CompleteAdding();

    private void Request(bool acquire)
    {
        _ = _thread.Value;

        try
        {
            _requests.Add(acquire);
        }
        catch (InvalidOperationException)
        {
            // Disposed: no thread is left to keep the computer awake.
        }
    }

    private Thread StartThread()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "DAQiFi keep-awake" };
        thread.Start();
        return thread;
    }

    private void Run()
    {
        var streams = 0;
        foreach (var acquire in _requests.GetConsumingEnumerable())
        {
            if (acquire)
            {
                if (++streams == 1)
                {
                    Apply(keepAwake: true);
                }
            }
            else if (streams > 0 && --streams == 0)
            {
                // A release with no stream held is ignored. Below zero, the next stream would
                // not keep the computer awake.
                Apply(keepAwake: false);
            }
        }
    }

    private void Apply(bool keepAwake)
    {
        try
        {
            _apply(keepAwake);
        }
        catch (Exception ex)
        {
            // An exception that leaves this thread ends the process.
            AppLogger.Instance.Warning(ex, keepAwake
                ? "Could not keep the computer awake while streaming."
                : "Could not let the computer sleep after streaming.");
        }
    }

    private static void SetWindowsExecutionState(bool keepAwake)
    {
        var flags = keepAwake
            ? NativeMethods.EsContinuous | NativeMethods.EsSystemRequired
            : NativeMethods.EsContinuous;

        if (NativeMethods.SetThreadExecutionState(flags) == 0)
        {
            AppLogger.Instance.Warning(keepAwake
                ? "Failed to set computer from sleeping while streaming."
                : "Failed to reset thread execution state to allow sleep.");
            return;
        }

        AppLogger.Instance.Information(keepAwake
            ? "Preventing computer sleep during streaming."
            : "Allowing computer sleep after streaming.");
    }

    /// <summary>Every head but Windows: nothing to set.</summary>
    private sealed class NoKeepAwake : IKeepAwake
    {
        public void Acquire()
        {
        }

        public void Release()
        {
        }
    }
}

internal static partial class NativeMethods
{
    // Prevents the system from entering sleep or hibernation.
    public const uint EsSystemRequired = 0x00000001;
    // Informs the system that the state should remain in effect until another call resets it.
    public const uint EsContinuous = 0x80000000;

    // kernel32.dll is Windows-only, so only the Windows KeepAwakeOwner calls this.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint SetThreadExecutionState(uint esFlags);
}
