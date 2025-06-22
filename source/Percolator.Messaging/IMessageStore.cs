using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Percolator.Messaging
{
    /// <summary>
    /// Defines the contract for a message storage mechanism.
    /// </summary>
    public interface IMessageStore
    {
        Task StoreDirectMessageAsync(DirectMessage message);
        Task StoreGroupMessageAsync(GroupMessage message);

        Task UpdateDirectMessageAsync(DirectMessage message);
        Task UpdateGroupMessageAsync(GroupMessage message);

        Task<DirectMessage?> GetDirectMessageAsync(Guid messageId);
        Task<GroupMessage?> GetGroupMessageAsync(Guid messageId);

        Task<IEnumerable<DirectMessage>> GetDirectMessagesAsync(string userA, string userB);
        Task<IEnumerable<GroupMessage>> GetGroupMessagesAsync(Guid groupId);
        Task<Group?> GetGroupAsync(Guid groupId);
        Task StoreGroupAsync(Group group);
        Task UpdateGroupAsync(Group group);
    }
}
