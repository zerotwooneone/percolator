using Percolator.Domain.Channels.Events;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Channels.Model;

public sealed class GroupChannel : AggregateRoot<ChannelId>
{
    public override ChannelId Id { get; }
    public string Name { get; private set; }
    public EpochNumber CurrentEpoch { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    private readonly List<ChannelMember> _members = [];
    public IReadOnlyCollection<ChannelMember> Members => _members.AsReadOnly();

    private readonly List<ChannelPayload> _payloads = [];
    public IReadOnlyList<ChannelPayload> Payloads => _payloads.AsReadOnly();

    private GroupChannel(
        ChannelId id,
        string name,
        EpochNumber initialEpoch,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Name = name;
        CurrentEpoch = initialEpoch;
        CreatedAtUtc = createdAtUtc;
        LastActivityUtc = createdAtUtc;
    }

    public static DomainResult<GroupChannel> CreateGenesis(
        ChannelId id,
        PublicIdentityId creatorId,
        string name,
        IDateTimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DomainResult<GroupChannel>.Failure(new DomainError("INVALID_NAME", "Channel name cannot be empty."));
        }

        var group = new GroupChannel(id, name, EpochNumber.Genesis, timeProvider.UtcNow);
        group._members.Add(new ChannelMember(creatorId, ChannelRole.Admin, timeProvider.UtcNow));

        return DomainResult<GroupChannel>.Success(group);
    }

    public DomainResult RenameChannel(
        PublicIdentityId actorId,
        string newName,
        IDateTimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return DomainResult.Failure(new DomainError("INVALID_NAME", "Channel name cannot be empty."));
        }

        var actor = _members.FirstOrDefault(m => m.Id == actorId);
        if (actor == null || actor.Role != ChannelRole.Admin)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_ROLE", "Only channel admins can rename the channel."));
        }

        if (Name == newName)
        {
            return DomainResult.Success();
        }

        Name = newName;
        CurrentEpoch = CurrentEpoch.Next();
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new ChannelRenamedEvent(Id, actorId, newName, CurrentEpoch, LastActivityUtc));

        return DomainResult.Success();
    }

    public DomainResult AddMember(
        PublicIdentityId actorId,
        PublicIdentityId newMemberId,
        ChannelRole role,
        IDateTimeProvider timeProvider)
    {
        var actor = _members.FirstOrDefault(m => m.Id == actorId);
        if (actor == null || actor.Role != ChannelRole.Admin)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_ROLE", "Only channel admins can add new members."));
        }

        if (_members.Any(m => m.Id == newMemberId))
        {
            return DomainResult.Failure(new DomainError("MEMBER_ALREADY_EXISTS", "Member is already part of this channel."));
        }

        var newMember = new ChannelMember(newMemberId, role, timeProvider.UtcNow);
        _members.Add(newMember);

        CurrentEpoch = CurrentEpoch.Next();
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new MemberJoinedEvent(Id, newMemberId, role, CurrentEpoch, LastActivityUtc));

        return DomainResult.Success();
    }

    public DomainResult ChangeMemberRole(
        PublicIdentityId actorId,
        PublicIdentityId targetMemberId,
        ChannelRole newRole,
        IDateTimeProvider timeProvider)
    {
        var actor = _members.FirstOrDefault(m => m.Id == actorId);
        if (actor == null || actor.Role != ChannelRole.Admin)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_ROLE", "Only channel admins can change member roles."));
        }

        var targetMember = _members.FirstOrDefault(m => m.Id == targetMemberId);
        if (targetMember == null)
        {
            return DomainResult.Failure(new DomainError("MEMBER_NOT_FOUND", "Target member does not exist in channel."));
        }

        if (targetMember.Role == newRole)
        {
            return DomainResult.Success();
        }

        if (targetMember.Role == ChannelRole.Admin && newRole != ChannelRole.Admin && _members.Count(m => m.Role == ChannelRole.Admin) <= 1)
        {
            return DomainResult.Failure(new DomainError("LAST_ADMIN_CANNOT_BE_DEMOTED", "Cannot demote the last remaining admin in the channel."));
        }

        targetMember.ChangeRole(newRole);
        CurrentEpoch = CurrentEpoch.Next();
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new ChannelMemberRoleChangedEvent(Id, targetMemberId, newRole, CurrentEpoch, LastActivityUtc));

        return DomainResult.Success();
    }

    public DomainResult RemoveMember(
        PublicIdentityId actorId,
        PublicIdentityId targetMemberId,
        IDateTimeProvider timeProvider)
    {
        var actor = _members.FirstOrDefault(m => m.Id == actorId);
        if (actor == null || actor.Role != ChannelRole.Admin)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_ROLE", "Only channel admins can remove members."));
        }

        var memberToRemove = _members.FirstOrDefault(m => m.Id == targetMemberId);
        if (memberToRemove == null)
        {
            return DomainResult.Failure(new DomainError("MEMBER_NOT_FOUND", "Member does not exist in channel."));
        }

        if (memberToRemove.Role == ChannelRole.Admin && _members.Count(m => m.Role == ChannelRole.Admin) <= 1)
        {
            return DomainResult.Failure(new DomainError("CANNOT_REMOVE_LAST_ADMIN", "Cannot remove the last remaining admin from the channel."));
        }

        _members.Remove(memberToRemove);

        CurrentEpoch = CurrentEpoch.Next();
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new MemberRemovedEvent(Id, targetMemberId, CurrentEpoch, LastActivityUtc));

        return DomainResult.Success();
    }

    public DomainResult LeaveChannel(
        PublicIdentityId memberId,
        IDateTimeProvider timeProvider)
    {
        var member = _members.FirstOrDefault(m => m.Id == memberId);
        if (member == null)
        {
            return DomainResult.Failure(new DomainError("MEMBER_NOT_FOUND", "Member does not exist in channel."));
        }

        if (member.Role == ChannelRole.Admin && _members.Count(m => m.Role == ChannelRole.Admin) <= 1 && _members.Count > 1)
        {
            return DomainResult.Failure(new DomainError("LAST_ADMIN_CANNOT_LEAVE", "The last remaining admin cannot leave the channel without promoting another member to admin first."));
        }

        _members.Remove(member);

        CurrentEpoch = CurrentEpoch.Next();
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new MemberRemovedEvent(Id, memberId, CurrentEpoch, LastActivityUtc));

        return DomainResult.Success();
    }

    public DomainResult AppendPayload(ChannelPayload payload, IDateTimeProvider timeProvider)
    {
        if (payload.ChannelId != Id)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Payload does not belong to this group channel."));
        }

        if (!_members.Any(m => m.Id == payload.AuthorId))
        {
            return DomainResult.Failure(new DomainError("SENDER_NOT_MEMBER", "Payload author is not an active member of this channel."));
        }

        _payloads.Add(payload);
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new PayloadAppendedEvent(Id, payload.Id, LastActivityUtc));

        return DomainResult.Success();
    }

    public void Rebase(EpochNumber newEpoch, IEnumerable<ChannelMember> currentMembers, IDateTimeProvider timeProvider)
    {
        _members.Clear();
        _members.AddRange(currentMembers);
        CurrentEpoch = newEpoch;
        LastActivityUtc = timeProvider.UtcNow;
    }
}
