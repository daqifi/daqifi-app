// Downstream addition — no upstream counterpart, so no `// @port:` marker.

namespace Daqifi.Desktop.Logger;

/// <summary>
/// Thrown by <see cref="SessionDataRepository.DeleteSession"/> when the stored row with the session's
/// ID is no longer the recording the caller holds: another writer deleted it, or deleted it and gave
/// its ID to a new recording, while the delete confirmation was open (#680 review). Nothing was
/// deleted. An expected condition, not a fault, so callers do not report it as an error.
/// </summary>
public sealed class SessionReplacedException : Exception
{
    public SessionReplacedException(int sessionId)
        : base($"Session {sessionId} is no longer the recording that was asked to be deleted, so nothing was deleted.")
    {
        SessionId = sessionId;
    }

    /// <summary>The ID the delete was asked for.</summary>
    public int SessionId { get; }
}
