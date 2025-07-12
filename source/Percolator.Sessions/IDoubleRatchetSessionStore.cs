namespace Percolator.Sessions;

/// <summary>
/// Provides a persistent storage mechanism for Double Ratchet session states.
/// The session state itself is treated as an opaque blob by this interface,
/// with the application layer being responsible for serialization and deserialization.
/// </summary>
public interface IDoubleRatchetSessionStore
{
    /// <summary>
    /// Retrieves the opaque session state for a given session ID.
    /// </summary>
    /// <param name="sessionId">The unique identifier for the session.</param>
    /// <returns>The session state, or null if not found.</returns>
    Task<SessionState?> GetSessionStateAsync(string sessionId);

    /// <summary>
    /// Saves the opaque session state for a given session ID.
    /// </summary>
    /// <param name="sessionId">The unique identifier for the session.</param>
    /// <param name="sessionState">The opaque session state to save.</param>
    Task SetSessionStateAsync(string sessionId, SessionState sessionState);
}