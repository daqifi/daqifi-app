using Daqifi.Desktop.Common.Loggers;

namespace Daqifi.Desktop.Device;

/// <summary>
/// Keeps the computer awake while a device streams to the app.
/// </summary>
internal interface IKeepAwake
{
    /// <summary>One more stream needs the computer awake.</summary>
    void Acquire();

    /// <summary>One stream no longer needs the computer awake.</summary>
    void Release();
}

/// <summary>
/// The <see cref="IKeepAwake"/> the app uses.
/// </summary>
internal sealed class KeepAwakeOwner : IKeepAwake, IDisposable
{
    private readonly Func<bool, bool> _apply;

    /// <param name="apply">
    /// Sets the state: <c>true</c> keeps the computer awake, <c>false</c> lets it sleep. Returns
    /// whether the call succeeded.
    /// </param>
    internal KeepAwakeOwner(Func<bool, bool> apply)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    /// <summary>
    /// The one the app uses. On Windows it sets the execution state; elsewhere it does nothing.
    /// </summary>
    public static IKeepAwake ForThisProcess { get; } =
        OperatingSystem.IsWindows() ? new KeepAwakeOwner(SetWindowsExecutionState) : new NoKeepAwake();

    public void Acquire() => _apply(true);

    public void Release() => _apply(false);

    public void Dispose()
    {
    }

    private static bool SetWindowsExecutionState(bool keepAwake)
    {
        var flags = keepAwake
            ? NativeMethods.EsContinuous | NativeMethods.EsSystemRequired
            : NativeMethods.EsContinuous;

        if (NativeMethods.SetThreadExecutionState(flags) == 0)
        {
            AppLogger.Instance.Warning(keepAwake
                ? "Failed to set computer from sleeping while streaming."
                : "Failed to reset thread execution state to allow sleep.");
            return false;
        }

        AppLogger.Instance.Information(keepAwake
            ? "Preventing computer sleep during streaming."
            : "Allowing computer sleep after streaming.");
        return true;
    }

    /// <summary>Every platform but Windows: nothing to set.</summary>
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
