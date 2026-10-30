using Percolator.Domain.Channels.Events;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Channels.Model;

public sealed class GroupChannel : AggregateRoot<ChannelId>
{
    public const int MaxRetainedPayloads = 1000;

    public override ChannelId Id { get; }
    public string Name { get; private set; }
    public EpochNumber CurrentEpoch { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    private readonly List<ChannelMember> _members = [];
    private readonly Dictionary<PublicIdentityId, ChannelMember> _membersById = [];
    public IReadOnlyCollection<ChannelMember> Members => _members.AsReadOnly();

    private readonly List<ChannelPayload> _payloads = [];
    private readonly HashSet<PayloadId> _payloadIds = [];
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
        var creatorMember = new ChannelMember(creatorId, ChannelRole.Admin, timeProvider.UtcNow);
        group._members.Add(creatorMember);
        group._membersById[creatorId] = creatorMember;

        return DomainResult<GroupChannel>.Success(group);
    }

    public static GroupChannel Rehydrate(
        ChannelId id,
        string name,
        EpochNumber epoch,
        DateTimeOffset createdAtUtc,
        DateTimeOffset lastActivityUtc,
        IEnumerable<ChannelMember> members,
        IEnumerable<ChannelPayload>? payloads = null)
    {
        var channel = new GroupChannel(id, name, epoch, createdAtUtc)
        {
            LastActivityUtc = lastActivityUtc
        };
        foreach (var member in members)
        {
            channel._members.Add(member);
            channel._membersById[member.Id] = member;
        }
        if (payloads != null)
        {
            foreach (var payload in payloads)
            {
                channel._payloads.Add(payload);
                channel._payloadIds.Add(payload.Id);
            }
        }
        return channel;
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

        if (!_membersById.TryGetValue(actorId, out var actor) || actor.Role != ChannelRole.Admin)
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
        if (!_membersById.TryGetValue(actorId, out var actor) || actor.Role != ChannelRole.Admin)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_ROLE", "Only channel admins can add new members."));
        }

        if (_membersById.ContainsKey(newMemberId))
        {
            return DomainResult.Failure(new DomainError("MEMBER_ALREADY_EXISTS", "Member is already part of this channel."));
        }

        var newMember = new ChannelMember(newMemberId, role, timeProvider.UtcNow);
        _members.Add(newMember);
        _membersById[newMemberId] = newMember;

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
        if (!_membersById.TryGetValue(actorId, out var actor) || actor.Role != ChannelRole.Admin)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_ROLE", "Only channel admins can change member roles."));
        }

        if (!_membersById.TryGetValue(targetMemberId, out var targetMember))
        {
            return DomainResult.Failure(new DomainError("MEMBER_NOT_FOUND", "Target member does not exist in channel."));
        }

        if (targetMember.Role == newRole)
        {
            return DomainResult.Success();
        }

        if (targetMember.Role == ChannelRole.Admin && newRole != ChannelRole.Admin && CountAdmins() <= 1)
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
        if (!_membersById.TryGetValue(actorId, out var actor) || actor.Role != ChannelRole.Admin)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_ROLE", "Only channel admins can remove members."));
        }

        if (!_membersById.TryGetValue(targetMemberId, out var memberToRemove))
        {
            return DomainResult.Failure(new DomainError("MEMBER_NOT_FOUND", "Member does not exist in channel."));
        }

        if (memberToRemove.Role == ChannelRole.Admin && CountAdmins() <= 1)
        {
            return DomainResult.Failure(new DomainError("CANNOT_REMOVE_LAST_ADMIN", "Cannot remove the last remaining admin from the channel."));
        }

        _members.Remove(memberToRemove);
        _membersById.Remove(targetMemberId);

        CurrentEpoch = CurrentEpoch.Next();
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new MemberRemovedEvent(Id, targetMemberId, CurrentEpoch, LastActivityUtc));

        return DomainResult.Success();
    }

    public DomainResult LeaveChannel(
        PublicIdentityId memberId,
        IDateTimeProvider timeProvider)
    {
        if (!_membersById.TryGetValue(memberId, out var member))
        {
            return DomainResult.Failure(new DomainError("MEMBER_NOT_FOUND", "Member does not exist in channel."));
        }

        if (member.Role == ChannelRole.Admin && CountAdmins() <= 1 && _members.Count > 1)
        {
            return DomainResult.Failure(new DomainError("LAST_ADMIN_CANNOT_LEAVE", "The last remaining admin cannot leave the channel without promoting another member to admin first."));
        }

        _members.Remove(member);
        _membersById.Remove(memberId);

        CurrentEpoch = CurrentEpoch.Next();
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new MemberRemovedEvent(Id, memberId, CurrentEpoch, LastActivityUtc));

        return DomainResult.Success();
    }

    public DomainResult AppendPayload(ChannelPayload payload, IDateTimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!payload.Id.IsValid)
        {
            return DomainResult.Failure(new DomainError("INVALID_PAYLOAD_ID", "Payload identifier cannot be empty."));
        }

        if (payload.ChannelId != Id)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Payload does not belong to this group channel."));
        }

        if (!_membersById.ContainsKey(payload.AuthorId))
        {
            return DomainResult.Failure(new DomainError("SENDER_NOT_MEMBER", "Payload author is not an active member of this channel."));
        }

        if (!_payloadIds.Add(payload.Id))
        {
            return DomainResult.Failure(new DomainError("DUPLICATE_PAYLOAD_ID", "A payload with this identifier has already been appended to the channel."));
        }

        while (_payloads.Count >= MaxRetainedPayloads)
        {
            var oldest = _payloads[0];
            _payloads.RemoveAt(0);
            _payloadIds.Remove(oldest.Id);
        }

        _payloads.Add(payload);
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new PayloadAppendedEvent(Id, payload.Id, LastActivityUtc));

        return DomainResult.Success();
    }

    public DomainResult Rebase(EpochNumber newEpoch, IEnumerable<ChannelMember> currentMembers, IDateTimeProvider timeProvider)
    {
        if (newEpoch.Value < CurrentEpoch.Value)
        {
            return DomainResult.Failure(new DomainError(
                "EPOCH_ROLLBACK_NOT_ALLOWED",
                $"Cannot rebase channel from epoch {CurrentEpoch.Value} back to {newEpoch.Value}."));
        }

        if (currentMembers == null)
        {
            return DomainResult.Failure(new DomainError(
                "EMPTY_ROSTER",
                "Rebased channel roster cannot be null or empty."));
        }

        var rosterList = currentMembers as IReadOnlyList<ChannelMember> ?? currentMembers.ToList();
        if (rosterList.Count == 0)
        {
            return DomainResult.Failure(new DomainError(
                "EMPTY_ROSTER",
                "Rebased channel roster must contain at least one member."));
        }

        bool hasAdmin = false;
        foreach (var m in rosterList)
        {
            if (m.Role == ChannelRole.Admin)
            {
                hasAdmin = true;
                break;
            }
        }

        if (!hasAdmin)
        {
            return DomainResult.Failure(new DomainError(
                "NO_ADMIN_IN_ROSTER",
                "Rebased channel roster must contain at least one administrator."));
        }

        _members.Clear();
        _membersById.Clear();
        foreach (var member in rosterList)
        {
            if (_membersById.TryAdd(member.Id, member))
            {
                _members.Add(member);
            }
        }

        CurrentEpoch = newEpoch;
        LastActivityUtc = timeProvider.UtcNow;
        return DomainResult.Success();
    }

    private int CountAdmins()
    {
        int count = 0;
        foreach (var m in _members)
        {
            if (m.Role == ChannelRole.Admin)
            {
                count++;
            }
        }

        return count;
    }
}
