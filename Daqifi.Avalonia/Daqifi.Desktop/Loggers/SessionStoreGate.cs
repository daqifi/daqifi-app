// Downstream addition — no upstream counterpart, so no `// @port:` marker.

namespace Daqifi.Desktop.Logger;

/// <summary>
/// The one owner of "who may write sessions into the database file now" in this app (#682).
/// DELETE ALL's purge replaces the file; an import writes sessions into it. The two must not
/// overlap: an import that writes while the purge moves the file fails with "disk I/O error" and
/// then writes on into the new file, and an import that writes into the new file before the purge
/// ends is deleted with it (#682 review).
/// </summary>
/// <remarks>
/// <para>Every import entry point claims an import before it writes, and the purge claims the store
/// before it starts. A claim is atomic, so there is no window between a check and the work. Imports
/// may overlap each other (two devices at once); the purge needs the store to itself. Dispose the
/// claim when the work ends, success or failure.</para>
/// <para>One instance per app shell (<c>DaqifiViewModel</c>), shared by the DEVICE LOGS pane, the
/// Logged Data pane and the import of earlier sessions. It does not cover a second DAQiFi process;
/// see <c>DatabaseMigrator.ReplaceWithEmptyDatabase</c>.</para>
/// </remarks>
public sealed class SessionStoreGate
{
    /// <summary>What an import that DELETE ALL holds back tells the user.</summary>
    public const string WaitForDeleteAll = "Wait for DELETE ALL to finish, then import again.";

    private readonly Lock _sync = new();
    private int _imports;
    private bool _purging;

    /// <summary>True while at least one import holds a claim.</summary>
    public bool IsImportRunning
    {
        get { lock (_sync) { return _imports > 0; } }
    }

    /// <summary>
    /// Claims a place for an import. Null while DELETE ALL holds the store: the import must not start.
    /// </summary>
    public IDisposable? TryBeginImport()
    {
        lock (_sync)
        {
            if (_purging) { return null; }
            _imports++;
        }

        return new Claim(() => { lock (_sync) { _imports--; } });
    }

    /// <summary>
    /// Claims the store for DELETE ALL's purge. Null while an import (or another purge) holds a claim:
    /// the purge must not start. While the claim is held, no import can start.
    /// </summary>
    public IDisposable? TryBeginPurge()
    {
        lock (_sync)
        {
            if (_purging || _imports > 0) { return null; }
            _purging = true;
        }

        return new Claim(() => { lock (_sync) { _purging = false; } });
    }

    /// <summary>Releases once, however many times it is disposed.</summary>
    private sealed class Claim(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
