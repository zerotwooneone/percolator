using System.Collections.Concurrent;
using Percolator.Sessions;

namespace Percolator.Application.Sessions
{
    public class InMemoryConversationStore : IConversationStore
    {
        private readonly ConcurrentDictionary<ConversationId, DirectConversation> _conversations = new();

        public Task SaveConversationAsync(DirectConversation conversation)
        {
            _conversations[conversation.Id] = conversation;
            return Task.CompletedTask;
        }

        public Task<DirectConversation?> GetConversationAsync(ConversationId conversationId)
        {
            _conversations.TryGetValue(conversationId, out var conversation);
            return Task.FromResult(conversation);
        }

        public Task DeleteConversationAsync(ConversationId conversationId)
        {
            _conversations.TryRemove(conversationId, out _);
            return Task.CompletedTask;
        }
    }
}