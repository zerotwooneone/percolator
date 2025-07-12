using Percolator.Cryptography;

namespace Percolator.Sessions;

/// <summary>
/// Defines the contract for storing and retrieving the state of Double Ratchet sessions.
/// </summary>
public interface IDoubleRatchetSessionStore
{
    /// <summary>
    /// Retrieves the state of a Double Ratchet session.
    /// </summary>
    /// <param name="sessionId">The unique identifier for the session.</param>
    /// <returns>The session state, or null if not found.</returns>
    Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(string sessionId);

    /// <summary>
    /// Stores the state of a Double Ratchet session.
    /// </summary>
    /// <param name="sessionId">The unique identifier for the session.</param>
    /// <param name="sessionState">The state to store.</param>
    Task SetSessionStateAsync(string sessionId, DoubleRatchetSession.DoubleRatchetSessionState sessionState);
}