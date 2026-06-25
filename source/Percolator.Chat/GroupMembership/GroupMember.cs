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
    public GroupParticipantId ParticipantId { get; private set; }
    public GroupMemberRole Role { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; }
    public DateTimeOffset? RemovedAtUtc { get; private set; }

    public GroupMember(Messaging.ValueObjects.ConversationId conversationId, GroupParticipantId participantId, GroupMemberRole role, DateTimeOffset joinedAtUtc, DateTimeOffset? removedAtUtc = null)
    {
        ConversationId = conversationId;
        ParticipantId = participantId;
        Role = role;
        JoinedAtUtc = joinedAtUtc;
        RemovedAtUtc = removedAtUtc;
    }

    /// <summary>
    /// Upgrades an unresolved member to have a local peer identity.
    /// </summary>
    public void ResolveLocalPeerId(ChatPeerId localId)
    {
        if (ParticipantId.LocalPeerId is not null)
            throw new InvalidOperationException("Member already has a resolved local peer ID.");
        
        ParticipantId = new GroupParticipantId(ParticipantId.Pkh, localId);
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
