using Percolator.Chat.ValueObjects;
using Percolator.Identity;

namespace Percolator.Chat;

/// <summary>
/// The role of a member in a group conversation.
/// </summary>
public enum GroupMemberRole
{
    Member = 0,
    Admin = 1
}

/// <summary>
/// Domain model for a member of a group conversation.
/// </summary>
public sealed class GroupMember
{
    public ConversationId ConversationId { get; }
    public PeerId PeerId { get; }
    public GroupMemberRole Role { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; }
    public DateTimeOffset? RemovedAtUtc { get; private set; }

    public GroupMember(ConversationId conversationId, PeerId peerId, GroupMemberRole role, DateTimeOffset joinedAtUtc, DateTimeOffset? removedAtUtc = null)
    {
        ConversationId = conversationId;
        PeerId = peerId;
        Role = role;
        JoinedAtUtc = joinedAtUtc;
        RemovedAtUtc = removedAtUtc;
    }

    public void Remove(DateTimeOffset when)
    {
        RemovedAtUtc = when;
    }

    public void PromoteToAdmin()
    {
        Role = GroupMemberRole.Admin;
    }
}
