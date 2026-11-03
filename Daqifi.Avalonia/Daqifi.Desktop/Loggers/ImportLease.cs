// Downstream addition (#353, #510) — no upstream counterpart, so no `// @port:` marker.

namespace Daqifi.Desktop.Logger;

/// <summary>
/// Tells every copy of the app sharing a database whether an import into it is still running, so
/// none mistakes another's live import for an abandoned one.
/// </summary>
/// <remarks>
/// <para>
/// An import writes its session as <see cref="SessionStatus.Importing"/> and flips it when it ends.
/// A row still saying <c>Importing</c> after a crash is cleaned up later, which is only safe if no
/// process is still writing it. Each import therefore holds a lock file beside the database for as
/// long as it can leave such a row, and the cleanup reclaims rows only when it can take that file
/// for itself (<see cref="TryReclaim"/>), that is, when no copy of the app is importing.
/// </para>
/// <para>
/// A file rather than a named mutex: several imports in one or more processes may hold it at once
/// (a shared lock) while the cleanup needs it alone (an exclusive one), and a mutex offers only the
/// latter. A file handle is also not tied to the thread that took it, which matters for imports that
/// await. The OS closes the handle when a process dies, so a crashed import never keeps it held.
/// On Windows this is a share-mode conflict; elsewhere .NET maps the same share modes onto
/// <c>flock</c>.
/// </para>
/// <para>
/// The files sit next to the database they guard (<c>DAQiFiDatabase.db-imports.lock</c>), so two
/// databases never share one. They are empty and left in place.
/// </para>
/// </remarks>
internal static class ImportLease
{
    /// <summary>Held, shared, by every import that may leave an <c>Importing</c> row.</summary>
    internal const string LiveSuffix = "-imports.lock";

    /// <summary>Held alone by an import of earlier sessions, so two never copy the same sessions.</summary>
    internal const string PreviousSessionsSuffix = "-previous-import.lock";

    /// <summary>How long an import waits for a cleanup that holds the lock. A cleanup is one UPDATE.</summary>
    internal static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Marks an import as running for as long as the result is held. Waits while a cleanup holds
    /// the lock. Throws <see cref="IOException"/> if it is still held after <paramref name="wait"/>.
    /// </summary>
    /// <param name="databasePath">The database being imported into; no lock when it is not a file.</param>
    /// <param name="wait">How long to wait; <see cref="CleanupWait"/> when omitted.</param>
    public static IDisposable HoldLive(string? databasePath, TimeSpan? wait = null)
    {
        if (LockPath(databasePath, LiveSuffix) is not { } path)
        {
            return NoLock.Instance;
        }

        var deadline = DateTime.UtcNow + (wait ?? CleanupWait);
        while (true)
        {
            try
            {
                return Open(path, FileShare.ReadWrite);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Takes the lock alone, for cleaning up rows an import left behind. <c>null</c> when any import
    /// in any copy of the app (this one included) is running.
    /// </summary>
    public static IDisposable? TryReclaim(string? databasePath) => TryAlone(databasePath, LiveSuffix);

    /// <summary>
    /// Takes the earlier-sessions import lock alone. <c>null</c> when another such import is running.
    /// </summary>
    public static IDisposable? TryHoldPreviousSessionsImport(string databasePath) =>
        TryAlone(databasePath, PreviousSessionsSuffix);

    private static IDisposable? TryAlone(string? databasePath, string suffix)
    {
        if (LockPath(databasePath, suffix) is not { } path)
        {
            return NoLock.Instance;
        }

        try
        {
            return Open(path, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static FileStream Open(string path, FileShare share) =>
        new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, share);

    /// <summary>The lock file for a database, or <c>null</c> for an in-memory or unnamed one.</summary>
    private static string? LockPath(string? databasePath, string suffix)
    {
        if (string.IsNullOrWhiteSpace(databasePath)
            || databasePath.StartsWith(":memory:", StringComparison.Ordinal)
            || databasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(databasePath) + suffix;
    }

    private sealed class NoLock : IDisposable
    {
        public static readonly NoLock Instance = new();

        public void Dispose()
        {
        }
    }
}
