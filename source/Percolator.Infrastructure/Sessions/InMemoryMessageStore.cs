using System.Collections.Concurrent;
using Percolator.Sessions;

namespace Percolator.Infrastructure.Sessions;

/// <summary>
/// An in-memory, thread-safe implementation of the message store for demonstration and testing purposes.
/// </summary>
public class InMemoryMessageStore : IMessageStore
{
    private readonly ConcurrentDictionary<ConversationId, DirectConversation> _directConversations = new();
    private readonly ConcurrentDictionary<ConversationId, GroupConversation> _groupConversations = new();
    private readonly ConcurrentDictionary<MessageId, DirectMessage> _directMessages = new();
    private readonly ConcurrentDictionary<MessageId, GroupMessage> _groupMessages = new();

    // Direct Conversation operations
    public Task StoreDirectConversationAsync(DirectConversation conversation)
    {
        _directConversations.TryAdd(conversation.Id, conversation);
        return Task.CompletedTask;
    }

    public Task UpdateDirectConversationAsync(DirectConversation conversation)
    {
        _directConversations[conversation.Id] = conversation;
        return Task.CompletedTask;
    }

    public Task<DirectConversation?> GetDirectConversationAsync(ConversationId conversationId)
    {
        _directConversations.TryGetValue(conversationId, out var conversation);
        return Task.FromResult(conversation);
    }

    public Task<DirectConversation?> GetDirectConversationByPeerIdAsync(PeerId peerId)
    {
        var conversation = _directConversations.Values.FirstOrDefault(c => c.RemotePeerId == peerId);
        return Task.FromResult(conversation);
    }

    public Task<IEnumerable<DirectConversation>> GetAllDirectConversationsAsync()
    {
        return Task.FromResult<IEnumerable<DirectConversation>>(_directConversations.Values.ToList());
    }

    public Task<DirectConversation?> GetConversationWithPeerAsync(PeerId peerId, CancellationToken cancellationToken)
    {
        var conversation = _directConversations.Values
            .FirstOrDefault(c => c.LocalPeerId.Equals(peerId) || c.RemotePeerId.Equals(peerId));
        return Task.FromResult(conversation);
    }

    // Group Conversation operations
    public Task StoreGroupConversationAsync(GroupConversation group)
    {
        _groupConversations.TryAdd(group.Id, group);
        return Task.CompletedTask;
    }

    public Task<GroupConversation?> GetGroupConversationAsync(ConversationId groupId)
    {
        _groupConversations.TryGetValue(groupId, out var group);
        return Task.FromResult(group);
    }

    public Task<IEnumerable<GroupConversation>> GetAllGroupConversationsAsync()
    {
        return Task.FromResult<IEnumerable<GroupConversation>>(_groupConversations.Values.ToList());
    }

    // Message operations
    public Task StoreDirectMessageAsync(DirectMessage message)
    {
        _directMessages.TryAdd(message.Id, message);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<DirectMessage>> GetDirectMessagesAsync(ConversationId conversationId)
    {
        var messages = _directMessages.Values
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Timestamp)
            .ToList();
        return Task.FromResult<IEnumerable<DirectMessage>>(messages);
    }

    public Task StoreGroupMessageAsync(GroupMessage message)
    {
        _groupMessages.TryAdd(message.Id, message);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<GroupMessage>> GetGroupMessagesAsync(ConversationId groupConversationId)
    {
        var messages = _groupMessages.Values
            .Where(m => m.GroupConversationId == groupConversationId)
            .OrderBy(m => m.Timestamp)
            .ToList();
        return Task.FromResult<IEnumerable<GroupMessage>>(messages);
    }
}
