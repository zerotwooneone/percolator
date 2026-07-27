using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.GroupLedger;

/// <summary>
/// Represents a group conversation with variable membership, roles, and semantic state.
/// Enforces admin invariants and epoch-based mutation tracking.
/// Cryptographic primitives like GroupMasterKey and AuthCredentialMac belong in the Cryptography domain.
/// </summary>
public sealed class GroupConversation
{
    private readonly List<GroupMember> _members = new();

    public ConversationId Id { get; }
    public GroupName Name { get; private set; }
    public GroupEpoch CurrentEpoch { get; private set; }
    public GroupAvatarId AvatarId { get; private set; }
    public IReadOnlyCollection<GroupMember> Members => _members.AsReadOnly();

    private GroupConversation(
        ConversationId id,
        GroupName name,
        GroupEpoch currentEpoch,
        GroupAvatarId avatarId,
        IEnumerable<GroupMember> members)
    {
        Id = id;
        Name = name;
        CurrentEpoch = currentEpoch;
        AvatarId = avatarId;
        _members.AddRange(members);
    }

    /// <summary>
    /// Creates a new group conversation with the creator as the initial admin.
    /// </summary>
    public static GroupConversation CreateNew(
        ConversationId id,
        GroupName name,
        ParticipantId creatorParticipantId,
        GroupAvatarId avatarId)
    {
        var creatorMember = new GroupMember(
            id,
            creatorParticipantId,
            GroupMemberRole.Admin,
            DateTimeOffset.UtcNow);

        return new GroupConversation(
            id,
            name,
            new GroupEpoch(1),
            avatarId,
            new[] { creatorMember });
    }

    /// <summary>
    /// Renames the group. Only admins can perform this action.
    /// </summary>
    public void RenameGroup(ParticipantId actorParticipantId, GroupName newName)
    {
        var actor = _members.FirstOrDefault(m => m.ParticipantId.PublicIdentityId == actorParticipantId.PublicIdentityId && m.RemovedAtUtc == null);
        if (actor == null || actor.Role != GroupMemberRole.Admin)
        {
            throw new UnauthorizedDomainException("Only admins can rename the group.");
        }

        Name = newName;
        CurrentEpoch = new GroupEpoch(CurrentEpoch.Value + 1);
    }

    /// <summary>
    /// Updates the group avatar. Only admins can perform this action.
    /// </summary>
    public void UpdateAvatar(ParticipantId actorParticipantId, GroupAvatarId newAvatarId)
    {
        var actor = _members.FirstOrDefault(m => m.ParticipantId.PublicIdentityId == actorParticipantId.PublicIdentityId && m.RemovedAtUtc == null);
        if (actor == null || actor.Role != GroupMemberRole.Admin)
        {
            throw new UnauthorizedDomainException("Only admins can update the group avatar.");
        }

        AvatarId = newAvatarId;
        CurrentEpoch = new GroupEpoch(CurrentEpoch.Value + 1);
    }

    /// <summary>
    /// Adds a new member to the group. Only admins can perform this action.
    /// </summary>
    public void AddMember(ParticipantId actorParticipantId, ParticipantId newMemberParticipantId)
    {
        var actor = _members.FirstOrDefault(m => m.ParticipantId.PublicIdentityId == actorParticipantId.PublicIdentityId && m.RemovedAtUtc == null);
        if (actor == null || actor.Role != GroupMemberRole.Admin)
        {
            throw new UnauthorizedDomainException("Only admins can add members to the group.");
        }

        if (_members.Any(m => m.ParticipantId.PublicIdentityId == newMemberParticipantId.PublicIdentityId && m.RemovedAtUtc == null))
        {
            throw new UnauthorizedDomainException("Member is already in the group.");
        }

        var newMember = new GroupMember(
            Id,
            newMemberParticipantId,
            GroupMemberRole.Member,
            DateTimeOffset.UtcNow);
        _members.Add(newMember);
        CurrentEpoch = new GroupEpoch(CurrentEpoch.Value + 1);
    }

    /// <summary>
    /// Removes a member from the group. Only admins can perform this action.
    /// </summary>
    public void RemoveMember(ParticipantId actorParticipantId, ParticipantId targetParticipantId)
    {
        var actor = _members.FirstOrDefault(m => m.ParticipantId.PublicIdentityId == actorParticipantId.PublicIdentityId && m.RemovedAtUtc == null);
        if (actor == null || actor.Role != GroupMemberRole.Admin)
        {
            throw new UnauthorizedDomainException("Only admins can remove members from the group.");
        }

        var target = _members.FirstOrDefault(m => m.ParticipantId.PublicIdentityId == targetParticipantId.PublicIdentityId && m.RemovedAtUtc == null);
        if (target == null)
        {
            throw new UnauthorizedDomainException("Target member not found in group.");
        }

        if (target.Role == GroupMemberRole.Admin)
        {
            var remainingAdmins = _members.Count(m => m.Role == GroupMemberRole.Admin && m.RemovedAtUtc == null);
            if (remainingAdmins <= 1)
            {
                throw new UnauthorizedDomainException("Cannot remove the last admin from the group.");
            }
        }

        target.Remove(DateTimeOffset.UtcNow);
        CurrentEpoch = new GroupEpoch(CurrentEpoch.Value + 1);
    }

    /// <summary>
    /// Leaves the group. A member cannot leave if they are the last admin.
    /// </summary>
    public void LeaveGroup(ParticipantId actorParticipantId)
    {
        var actor = _members.FirstOrDefault(m => m.ParticipantId.PublicIdentityId == actorParticipantId.PublicIdentityId && m.RemovedAtUtc == null);
        if (actor == null)
        {
            throw new UnauthorizedDomainException("Actor is not in the group.");
        }

        if (actor.Role == GroupMemberRole.Admin)
        {
            var remainingAdmins = _members.Count(m => m.Role == GroupMemberRole.Admin && m.RemovedAtUtc == null);
            if (remainingAdmins <= 1)
            {
                throw new UnauthorizedDomainException("Cannot leave as the last admin of the group.");
            }
        }

        actor.Remove(DateTimeOffset.UtcNow);
        CurrentEpoch = new GroupEpoch(CurrentEpoch.Value + 1);
    }

    /// <summary>
    /// Changes a member's role. Only admins can perform this action.
    /// </summary>
    public void ChangeMemberRole(ParticipantId actorParticipantId, ParticipantId targetParticipantId, GroupMemberRole newRole)
    {
        var actor = _members.FirstOrDefault(m => m.ParticipantId.PublicIdentityId == actorParticipantId.PublicIdentityId && m.RemovedAtUtc == null);
        if (actor == null || actor.Role != GroupMemberRole.Admin)
        {
            throw new UnauthorizedDomainException("Only admins can change member roles.");
        }

        var target = _members.FirstOrDefault(m => m.ParticipantId.PublicIdentityId == targetParticipantId.PublicIdentityId && m.RemovedAtUtc == null);
        if (target == null)
        {
            throw new UnauthorizedDomainException("Target member not found in group.");
        }

        if (newRole == GroupMemberRole.Member && target.Role == GroupMemberRole.Admin)
        {
            var remainingAdmins = _members.Count(m => m.Role == GroupMemberRole.Admin && m.RemovedAtUtc == null);
            if (remainingAdmins <= 1)
            {
                throw new UnauthorizedDomainException("Cannot demote the last admin of the group.");
            }
        }

        target.ChangeRole(newRole);
        CurrentEpoch = new GroupEpoch(CurrentEpoch.Value + 1);
    }
}
