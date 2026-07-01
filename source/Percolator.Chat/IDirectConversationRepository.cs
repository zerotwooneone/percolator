using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat;

/// <summary>
/// Repository for managing direct 1:1 conversations.
/// </summary>
public interface IDirectConversationRepository
{
    Task<DirectConversation?> GetByIdAsync(ConversationId id, ChatSelfId selfIdentityId, CancellationToken cancellationToken);
    Task AddAsync(DirectConversation conversation, ChatSelfId selfIdentityId, CancellationToken cancellationToken);
    Task<DirectConversation?> GetByParticipantPairAsync(ChatSelfId selfIdentityId, Guid otherPeerId, CancellationToken cancellationToken);
    Task UpsertDirectSessionMappingAsync(ChatSelfId selfIdentityId, Guid directSessionId, ConversationId conversationId, CancellationToken cancellationToken);
}
