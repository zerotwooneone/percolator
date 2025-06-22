using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Percolator.Messaging
{
    /// <summary>
    /// Defines the contract for group management operations.
    /// </summary>
    public interface IGroupService
    {
        Task<Group> CreateGroupAsync(string groupName, IEnumerable<string> initialMemberIds);
        Task AddMemberToGroupAsync(Guid groupId, string requesterId, string newMemberId);
        Task RemoveMemberFromGroupAsync(Guid groupId, string requesterId, string memberToRemoveId);
        Task RenameGroupAsync(Guid groupId, string requesterId, string newName);
        Task<Group?> GetGroupDetailsAsync(Guid groupId);
    }
}
