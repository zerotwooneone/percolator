namespace Percolator.Cryptography;

/// <summary>
/// Provides a persistent storage mechanism for Double Ratchet session states.
/// </summary>
public interface IDoubleRatchetSessionStore
{
    /// <summary>
    /// Retrieves the session state for a given session ID.
    /// </summary>
    /// <param name="sessionId">The unique identifier for the session (typically a GUID).</param>
    /// <returns>The session state, or null if not found.</returns>
    Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(SessionId sessionId);

    /// <summary>
    /// Saves the session state for a given session ID.
    /// </summary>
    /// <param name="sessionId">The unique identifier for the session.</param>
    /// <param name="sessionState">The session state to save.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetSessionStateAsync(SessionId sessionId, DoubleRatchetSession.DoubleRatchetSessionState sessionState);
}
