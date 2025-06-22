using System.Security;
using Percolator.Messaging;

namespace Percolator.Application.Messaging
{
    public class GroupService : IGroupService
    {
        private readonly IMessageStore _messageStore;

        public GroupService(IMessageStore messageStore)
        {
            _messageStore = messageStore;
        }

        public async Task<Group> CreateGroupAsync(string groupName, IEnumerable<string> initialMemberIds)
        {
            if (string.IsNullOrWhiteSpace(groupName))
            {
                throw new ArgumentException("Group name cannot be empty.", nameof(groupName));
            }

            var group = new Group(Guid.NewGuid(), groupName, initialMemberIds);
            await _messageStore.StoreGroupAsync(group);
            return group;
        }

        public async Task AddMemberToGroupAsync(Guid groupId, string requesterId, string newMemberId)
        {
            var group = await _messageStore.GetGroupAsync(groupId);
            if (group is null)
            {
                throw new KeyNotFoundException("Group not found.");
            }

            if (!group.MemberIds.Contains(requesterId))
            {
                throw new SecurityException("Only a current member can add a new member to the group.");
            }

            group.AddMember(newMemberId);
            await _messageStore.UpdateGroupAsync(group);
        }

        public async Task RemoveMemberFromGroupAsync(Guid groupId, string requesterId, string memberToRemoveId)
        {
            var group = await _messageStore.GetGroupAsync(groupId) ??
                throw new InvalidOperationException($"Group with ID {groupId} not found.");

            if (!group.MemberIds.Contains(requesterId))
            {
                throw new SecurityException("Only a current member can remove a member from the group.");
            }

            group.RemoveMember(memberToRemoveId);
            await _messageStore.UpdateGroupAsync(group);
        }

        public async Task RenameGroupAsync(Guid groupId, string requesterId, string newName)
        {
            var group = await _messageStore.GetGroupAsync(groupId) ??
                throw new InvalidOperationException($"Group with ID {groupId} not found.");

            if (!group.MemberIds.Contains(requesterId))
            {
                throw new SecurityException("Only members can rename the group.");
            }

            group.Rename(newName);

            await _messageStore.UpdateGroupAsync(group);
        }

        public async Task<Group?> GetGroupDetailsAsync(Guid groupId)
        {
            return await _messageStore.GetGroupAsync(groupId);
        }
    }
}
