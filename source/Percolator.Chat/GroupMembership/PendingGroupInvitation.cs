namespace Percolator.Chat.GroupMembership;

/// <summary>
/// The status of a pending group invitation.
/// </summary>
public enum PendingGroupInvitationStatus
{
    Pending = 0,
    Accepted = 1,
    Declined = 2
}

/// <summary>
/// Domain model for a pending group invitation received from a remote peer.
/// </summary>
public sealed class PendingGroupInvitation
{
    public Guid Id { get; }
    public Messaging.ValueObjects.ConversationId ConversationId { get; }
    public ChatPeerId InviterPeerId { get; }
    public byte[] CreatorIdentityKey { get; }
    public IReadOnlyList<byte[]> InitialMembers { get; }
    public string? GroupName { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public PendingGroupInvitationStatus Status { get; private set; }

    public PendingGroupInvitation(
        Guid id,
        Messaging.ValueObjects.ConversationId conversationId,
        ChatPeerId inviterPeerId,
        byte[] creatorIdentityKey,
        IReadOnlyList<byte[]> initialMembers,
        string? groupName,
        DateTimeOffset receivedAtUtc,
        PendingGroupInvitationStatus status = PendingGroupInvitationStatus.Pending)
    {
        Id = id;
        ConversationId = conversationId;
        InviterPeerId = inviterPeerId;
        CreatorIdentityKey = creatorIdentityKey ?? throw new ArgumentNullException(nameof(creatorIdentityKey));
        InitialMembers = initialMembers ?? throw new ArgumentNullException(nameof(initialMembers));
        GroupName = groupName;
        ReceivedAtUtc = receivedAtUtc;
        Status = status;
    }

    public void Accept()
    {
        if (Status != PendingGroupInvitationStatus.Pending)
            throw new InvalidOperationException("Can only accept pending invitations.");
        Status = PendingGroupInvitationStatus.Accepted;
    }

    public void Decline()
    {
        if (Status != PendingGroupInvitationStatus.Pending)
            throw new InvalidOperationException("Can only decline pending invitations.");
        Status = PendingGroupInvitationStatus.Declined;
    }
}
