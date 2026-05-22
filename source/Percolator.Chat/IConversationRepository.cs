using Percolator.Chat.ValueObjects;

namespace Percolator.Chat;

public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(ConversationId id, int selfIdentityId);
    Task AddAsync(Conversation conversation, int selfIdentityId);
    Task UpdateAsync(Conversation conversation, int selfIdentityId);

    // Look up a direct 1:1 conversation by participant pair (self identity's peer and the other peer)
    Task<Conversation?> GetByParticipantPairAsync(int selfIdentityId, Guid otherPeerId);

    // Persist or update mapping between a direct session and a conversation for a given self identity
    Task UpsertDirectSessionMappingAsync(int selfIdentityId, Guid directSessionId, ConversationId conversationId);
}