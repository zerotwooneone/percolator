using System.Net;
using Percolator.Chat.ValueObjects;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.Application.Sessions;

/// <summary>
/// Defines the contract for managing conversations.
/// </summary>
public interface IConversationService
{
    /// <summary>
    /// Attempts to locate an existing direct conversation for the given remote peer.
    /// Returns null if not found.
    /// </summary>
    Task<DirectSessionId?> GetExistingDirectConversationAsync(
        Peer remotePeer);

    /// <summary>
    /// Creates a new direct conversation (and establishes cryptographic session) with the remote peer.
    /// </summary>
    Task<DirectSessionId> CreateNewDirectConversationAsync(
        DnsEndPoint endpoint,
        Peer remotePeer);
}
