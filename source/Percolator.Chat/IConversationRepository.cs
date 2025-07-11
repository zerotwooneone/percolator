using Percolator.Chat.ValueObjects;

namespace Percolator.Chat;

public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(ConversationId id);
    Task<Conversation?> GetByChannelIdAsync(ChannelId id);
    Task AddAsync(Conversation conversation);
    Task UpdateAsync(Conversation conversation);
}
