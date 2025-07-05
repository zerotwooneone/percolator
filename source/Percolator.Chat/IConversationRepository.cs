using Percolator.Chat.ValueObjects;

namespace Percolator.Chat;

public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(ConversationId id);
    Task AddAsync(Conversation conversation);
    Task UpdateAsync(Conversation conversation);
}
