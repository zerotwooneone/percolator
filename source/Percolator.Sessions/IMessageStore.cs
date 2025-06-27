namespace Percolator.Sessions;

/// <summary>
/// Defines the contract for a storage mechanism for messaging entities.
/// </summary>
public interface IMessageStore
{
    // Direct Conversation operations
    Task StoreDirectConversationAsync(DirectConversation conversation);
    Task UpdateDirectConversationAsync(DirectConversation conversation);
    Task<DirectConversation?> GetDirectConversationAsync(ConversationId conversationId);
    Task<DirectConversation?> GetDirectConversationByPeerIdAsync(PeerId peerId);
    Task<IEnumerable<DirectConversation>> GetAllDirectConversationsAsync();

    // Group Conversation operations
    Task StoreGroupConversationAsync(GroupConversation group);
    Task<GroupConversation?> GetGroupConversationAsync(ConversationId groupId);
    Task<IEnumerable<GroupConversation>> GetAllGroupConversationsAsync();

    // Message operations
    Task StoreDirectMessageAsync(DirectMessage message);
    Task<IEnumerable<DirectMessage>> GetDirectMessagesAsync(ConversationId conversationId);
    Task StoreGroupMessageAsync(GroupMessage message);
    Task<IEnumerable<GroupMessage>> GetGroupMessagesAsync(ConversationId groupConversationId);
}
