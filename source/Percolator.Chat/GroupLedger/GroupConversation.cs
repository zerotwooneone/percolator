using System.Collections.ObjectModel;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Chat.Events;
using Percolator.Chat.SeedWork;

namespace Percolator.Chat.GroupLedger;

/// <summary>
/// Represents a group conversation with variable membership, roles, and cryptographic state.
/// Groups have an aggregate-level name and enforce admin invariants.
/// </summary>
public sealed class GroupConversation
{
    private readonly List<GroupMember> _members = new();
    private readonly List<IDomainEvent> _domainEvents = new();

    public Messaging.ValueObjects.ConversationId Id { get; }
    public GroupState State { get; private set; }
    public string? Name { get; private set; }
    public GroupParticipantId RelayIdentity { get; }
    public IReadOnlyList<GroupMember> Members => new ReadOnlyCollection<GroupMember>(_members);
    public IReadOnlyList<IDomainEvent> GetDomainEvents() => _domainEvents;
    public void ClearDomainEvents() => _domainEvents.Clear();

    public GroupConversation(
        Messaging.ValueObjects.ConversationId id,
        GroupState state,
        GroupParticipantId relayIdentity,
        IEnumerable<GroupMember> members,
        string? name = null)
    {
        var memberList = members.ToList();

        // Enforce invariants
        if (memberList.Count == 0)
        {
            throw new ArgumentException("A group conversation must have at least one member.", nameof(members));
        }

        if (memberList.Any(m => m.RemovedAtUtc != null))
        {
            throw new ArgumentException("Cannot add removed members to a group conversation.", nameof(members));
        }

        if (memberList.Select(m => m.ParticipantId.Pkh).Distinct().Count() != memberList.Count)
        {
            throw new ArgumentException("A group conversation cannot have duplicate members.", nameof(members));
        }

        if (!memberList.Any(m => m.Role == GroupMemberRole.Admin))
        {
            throw new ArgumentException("A group conversation must have at least one admin.", nameof(members));
        }

        Id = id;
        State = state;
        RelayIdentity = relayIdentity;
        Name = name;
        _members.AddRange(memberList);
        
        // Register domain event for group provisioning
        _domainEvents.Add(new GroupProvisioningRequestedDomainEvent(
            Id,
            State.PublicParams,
            memberList.Select(m => m.ParticipantId.Pkh).ToList()));
    }

    public void AddMember(GroupMember member)
    {
        if (_members.Any(m => m.ParticipantId.Pkh.Span.SequenceEqual(member.ParticipantId.Pkh.Span) && m.RemovedAtUtc == null))
        {
            throw new InvalidOperationException("Member is already in the group.");
        }

        _members.Add(member);
    }

    public void InviteMember(GroupParticipantId participantId, ChatSenderKeyDistributionMessageBytes distributionMessage)
    {
        if (_members.Any(m => m.ParticipantId.Pkh.Span.SequenceEqual(participantId.Pkh.Span) && m.RemovedAtUtc == null))
        {
            throw new InvalidOperationException("Member is already in the group.");
        }

        var member = new GroupMember(Id, participantId, GroupMemberRole.Member, DateTimeOffset.UtcNow);
        _members.Add(member);
        
        // Register domain event for member invitation
        _domainEvents.Add(new MemberInvitedDomainEvent(
            Id,
            participantId,
            distributionMessage,
            RelayIdentity.Pkh));
    }

    public void RemoveMember(GroupParticipantId participantId, DateTimeOffset when)
    {
        var member = _members.FirstOrDefault(m => m.ParticipantId.Pkh.Span.SequenceEqual(participantId.Pkh.Span) && m.RemovedAtUtc == null);
        if (member == null)
        {
            throw new InvalidOperationException("Member not found in group.");
        }

        // Ensure at least one admin remains
        if (member.Role == GroupMemberRole.Admin)
        {
            var remainingAdmins = _members.Count(m => m.Role == GroupMemberRole.Admin && !m.ParticipantId.Pkh.Span.SequenceEqual(participantId.Pkh.Span) && m.RemovedAtUtc == null);
            if (remainingAdmins == 0)
                throw new InvalidOperationException("Cannot remove the last admin from the group.");
        }

        member.Remove(when);
    }

    public void ChangeName(string? newName, DateTimeOffset when)
    {
        State.ChangeName(newName, when);
        Name = newName;
    }

    public void IncrementEpoch(DateTimeOffset when)
    {
        State.IncrementEpoch(when);
    }
}
