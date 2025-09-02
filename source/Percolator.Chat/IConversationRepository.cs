using Percolator.Chat.ValueObjects;

namespace Percolator.Chat;

public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(ConversationId id, int selfIdentityId);
    Task AddAsync(Conversation conversation, int selfIdentityId);
    Task UpdateAsync(Conversation conversation, int selfIdentityId);
}