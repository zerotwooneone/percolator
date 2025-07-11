using System.Net;
using Percolator.Chat.ValueObjects;
using Percolator.Network;

namespace Percolator.Application.Sessions;

/// <summary>
/// Defines the contract for managing conversations.
/// </summary>
public interface IConversationService
{
    /// <summary>
    /// Creates a new direct conversation with a remote peer by connecting to the specified host and port.
    /// </summary>
    /// <returns>The unique ID of the direct conversation.</returns>
    Task<ConversationId> CreateDirectConversationAsync(DnsEndPoint endpoint, string peerName, TlsCertificate? tlsCertificate = null);

    /// <summary>
    /// Gets the last active direct conversation ID for a given peer.
    /// </summary>
    /// <param name="peerId">The ID of the peer.</param>
    /// <returns>The conversation ID, or null if no active conversation is found.</returns>
    Task<ConversationId?> GetLastActiveConversationIdAsync(Guid peerId);
}
