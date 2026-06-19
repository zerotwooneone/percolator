using System.Collections.ObjectModel;
using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.GroupLedger;

/// <summary>
/// Represents a group conversation with variable membership, roles, and cryptographic state.
/// Groups have an aggregate-level name and enforce admin invariants.
/// </summary>
public sealed class GroupConversation
{
    private readonly List<GroupMember> _members = new();

    public Messaging.ValueObjects.ConversationId Id { get; }
    public GroupState State { get; private set; }
    public string? Name { get; private set; }
    public IReadOnlyList<GroupMember> Members => new ReadOnlyCollection<GroupMember>(_members);

    public GroupConversation(
        Messaging.ValueObjects.ConversationId id,
        GroupState state,
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

        if (memberList.Select(m => m.PeerId).Distinct().Count() != memberList.Count)
        {
            throw new ArgumentException("A group conversation cannot have duplicate members.", nameof(members));
        }

        if (!memberList.Any(m => m.Role == GroupMemberRole.Admin))
        {
            throw new ArgumentException("A group conversation must have at least one admin.", nameof(members));
        }

        Id = id;
        State = state;
        Name = name;
        _members.AddRange(memberList);
    }

    public void AddMember(GroupMember member)
    {
        if (_members.Any(m => m.PeerId == member.PeerId && m.RemovedAtUtc == null))
        {
            throw new InvalidOperationException("Member is already in the group.");
        }

        _members.Add(member);
    }

    public void RemoveMember(GroupMembership.ChatPeerId peerId, DateTimeOffset when)
    {
        var member = _members.FirstOrDefault(m => m.PeerId.Value == peerId.Value && m.RemovedAtUtc == null);
        if (member == null)
        {
            throw new InvalidOperationException("Member not found in group.");
        }

        // Ensure at least one admin remains
        if (member.Role == GroupMemberRole.Admin)
        {
            var remainingAdmins = _members.Count(m => m.Role == GroupMemberRole.Admin && m.PeerId.Value != peerId.Value && m.RemovedAtUtc == null);
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
