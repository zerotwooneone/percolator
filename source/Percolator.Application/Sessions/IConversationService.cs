using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Sessions;

/// <summary>
/// Defines the contract for managing conversations.
/// </summary>
public interface IConversationService
{
    /// <summary>
    /// Creates a new direct conversation with a remote peer by connecting to the specified host and port.
    /// </summary>
    /// <param name="host">The host of the remote peer.</param>
    /// <param name="port">The port of the remote peer.</param>
    /// <param name="remotePublicIdentityKey">The remote peer's public identity signing key, encoded as a Base64 string.</param>
    /// <returns>The unique ID of the direct conversation.</returns>
    Task<ConversationId> CreateDirectConversationAsync(string host, int port, string remotePublicIdentityKey);

    /// <summary>
    /// Gets the last active direct conversation ID for a given peer.
    /// </summary>
    /// <param name="peerId">The ID of the peer.</param>
    /// <returns>The conversation ID, or null if no active conversation is found.</returns>
    Task<ConversationId?> GetLastActiveConversationIdAsync(Guid peerId);
}
