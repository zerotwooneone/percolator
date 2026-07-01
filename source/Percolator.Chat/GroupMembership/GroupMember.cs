namespace Percolator.Chat.GroupMembership;

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
    public Messaging.ValueObjects.ConversationId ConversationId { get; }
    public ParticipantId ParticipantId { get; }
    public GroupMemberRole Role { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; }
    public DateTimeOffset? RemovedAtUtc { get; private set; }

    public GroupMember(Messaging.ValueObjects.ConversationId conversationId, ParticipantId participantId, GroupMemberRole role, DateTimeOffset joinedAtUtc, DateTimeOffset? removedAtUtc = null)
    {
        ConversationId = conversationId;
        ParticipantId = participantId;
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
