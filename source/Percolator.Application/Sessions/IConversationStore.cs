using Percolator.Sessions;

namespace Percolator.Application.Sessions
{
    public interface IConversationStore
    {
        Task SaveConversationAsync(DirectConversation conversation);
        Task<DirectConversation?> GetConversationAsync(ConversationId conversationId);
        Task DeleteConversationAsync(ConversationId conversationId);
    }
}