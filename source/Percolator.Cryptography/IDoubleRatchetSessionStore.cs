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
    /// <param name="selfIdentityId">The active SelfIdentity scope.</param>
    /// <returns>The session state, or null if not found.</returns>
    Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(SessionId sessionId, int selfIdentityId);

    /// <summary>
    /// Saves the session state for a given session ID.
    /// </summary>
    /// <param name="sessionId">The unique identifier for the session.</param>
    /// <param name="sessionState">The session state to save.</param>
    /// <param name="selfIdentityId">The active SelfIdentity scope.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetSessionStateAsync(SessionId sessionId, DoubleRatchetSession.DoubleRatchetSessionState sessionState, int selfIdentityId);

    /// <summary>
    /// Enumerates all session IDs scoped to the given SelfIdentity.
    /// </summary>
    Task<IReadOnlyList<SessionId>> GetAllSessionIdsAsync(int selfIdentityId);

    /// <summary>
    /// Finds a session by its remote ratchet key.
    /// </summary>
    /// <param name="remoteRatchetKey">The remote ratchet key to search for.</param>
    /// <param name="selfIdentityId">The active SelfIdentity scope.</param>
    /// <returns>The session state if found, or null if not found.</returns>
    Task<DoubleRatchetSession.DoubleRatchetSessionState?> FindByRemoteRatchetKeyAsync(PreKey remoteRatchetKey, int selfIdentityId);
}
