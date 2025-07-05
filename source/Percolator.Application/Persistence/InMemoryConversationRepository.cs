using System.Collections.Concurrent;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Persistence;

public class InMemoryConversationRepository : IConversationRepository
{
    private readonly ConcurrentDictionary<ConversationId, Conversation> _conversations = new();

    public Task<Conversation?> GetByIdAsync(ConversationId id)
    {
        _conversations.TryGetValue(id, out var conversation);
        return Task.FromResult(conversation);
    }

    public Task AddAsync(Conversation conversation)
    {
        _conversations.TryAdd(conversation.Id, conversation);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Conversation conversation)
    {
        _conversations[conversation.Id] = conversation;
        return Task.CompletedTask;
    }
}
