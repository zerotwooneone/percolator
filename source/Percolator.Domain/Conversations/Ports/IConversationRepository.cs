using Percolator.Domain.Conversations.Model;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Conversations.Ports;

public interface IConversationRepository
{
    Task<DirectConversation?> GetDirectByIdAsync(ConversationId id, CancellationToken cancellationToken = default);
    Task<DirectConversation?> GetDirectByPeerAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default);
    Task<GroupConversation?> GetGroupByIdAsync(ConversationId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupConversation>> GetAllGroupsForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default);
    Task SaveDirectAsync(DirectConversation conversation, CancellationToken cancellationToken = default);
    Task SaveGroupAsync(GroupConversation conversation, CancellationToken cancellationToken = default);
}
