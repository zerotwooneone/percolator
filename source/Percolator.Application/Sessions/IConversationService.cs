using System.Net;
using Percolator.Chat.ValueObjects;
using Percolator.Identity;

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
    Task<ConversationId> CreateDirectConversationAsync(
        DnsEndPoint endpoint, 
        string remotePeerName);
}
