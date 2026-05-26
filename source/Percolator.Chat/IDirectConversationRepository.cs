using Percolator.Chat.ValueObjects;

namespace Percolator.Chat;

/// <summary>
/// Repository for managing direct 1:1 conversations.
/// </summary>
public interface IDirectConversationRepository
{
    Task<DirectConversation?> GetByIdAsync(ConversationId id, int selfIdentityId, CancellationToken cancellationToken);
    Task AddAsync(DirectConversation conversation, int selfIdentityId, CancellationToken cancellationToken);
    Task<DirectConversation?> GetByParticipantPairAsync(int selfIdentityId, Guid otherPeerId, CancellationToken cancellationToken);
    Task UpsertDirectSessionMappingAsync(int selfIdentityId, Guid directSessionId, ConversationId conversationId, CancellationToken cancellationToken);
}
