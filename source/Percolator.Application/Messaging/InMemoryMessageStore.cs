using System.Collections.Concurrent;
using Percolator.Messaging;

namespace Percolator.Application.Messaging
{
    /// <summary>
    /// An in-memory, thread-safe implementation of the message store for demonstration and testing purposes.
    /// </summary>
    public class InMemoryMessageStore : IMessageStore
    {
        private readonly ConcurrentDictionary<Guid, DirectMessage> _directMessages = new();
        private readonly ConcurrentDictionary<Guid, GroupMessage> _groupMessages = new();
        private readonly ConcurrentDictionary<Guid, Group> _groups = new();

        public Task StoreDirectMessageAsync(DirectMessage message)
        {
            _directMessages[message.Id] = message;
            return Task.CompletedTask;
        }

        public Task StoreGroupMessageAsync(GroupMessage message)
        {
            _groupMessages[message.Id] = message;
            return Task.CompletedTask;
        }

        public Task UpdateDirectMessageAsync(DirectMessage message)
        {
            _directMessages[message.Id] = message;
            return Task.CompletedTask;
        }

        public Task UpdateGroupMessageAsync(GroupMessage message)
        {
            _groupMessages[message.Id] = message;
            return Task.CompletedTask;
        }

        public Task<DirectMessage?> GetDirectMessageAsync(Guid messageId)
        {
            _directMessages.TryGetValue(messageId, out var message);
            return Task.FromResult<DirectMessage?>(message);
        }

        public Task<GroupMessage?> GetGroupMessageAsync(Guid messageId)
        {
            _groupMessages.TryGetValue(messageId, out var message);
            return Task.FromResult<GroupMessage?>(message);
        }

        public Task<IEnumerable<DirectMessage>> GetDirectMessagesAsync(string userA, string userB)
        {
            var messages = _directMessages.Values
                .Where(m =>
                    (m.SenderId == userA && m.RecipientId == userB) ||
                    (m.SenderId == userB && m.RecipientId == userA))
                .OrderBy(m => m.TimestampUtc);
            return Task.FromResult<IEnumerable<DirectMessage>>(messages);
        }

        public Task<IEnumerable<GroupMessage>> GetGroupMessagesAsync(Guid groupId)
        {
            var messages = _groupMessages.Values
                .Where(m => m.GroupId == groupId)
                .OrderBy(m => m.TimestampUtc);
            return Task.FromResult<IEnumerable<GroupMessage>>(messages);
        }

        public Task<Group?> GetGroupAsync(Guid groupId)
        {
            _groups.TryGetValue(groupId, out var group);
            return Task.FromResult<Group?>(group);
        }

        public Task StoreGroupAsync(Group group)
        {
            _groups[group.Id] = group;
            return Task.CompletedTask;
        }

        public Task UpdateGroupAsync(Group group)
        {
            _groups[group.Id] = group;
            return Task.CompletedTask;
        }
    }
}
