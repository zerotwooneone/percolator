using Percolator.Sessions;
using IdentityPeerId = Percolator.Identity.PeerId;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Sessions;

/// <summary>
/// Defines the contract for managing conversations.
/// </summary>
public interface IConversationService
{
    /// <summary>
    /// Creates a new direct conversation with a peer if one does not already exist.
    /// If a conversation already exists, its ID is returned.
    /// </summary>
    /// <param name="peerId">The ID of the peer to create a conversation with.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The unique ID of the direct conversation.</returns>
    Task<ConversationId> CreateDirectConversationAsync(IdentityPeerId peerId, CancellationToken cancellationToken);
}
