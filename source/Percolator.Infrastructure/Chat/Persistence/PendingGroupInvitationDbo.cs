using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Infrastructure.Chat.Persistence;

/// <summary>
/// Represents a pending group invitation received from a remote peer.
/// </summary>
public class PendingGroupInvitationDbo
{
    /// <summary>
    /// Primary key (GUID).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The conversation ID (indexed).
    /// </summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// The peer ID of the inviter.
    /// </summary>
    public uint InviterPeerId { get; set; }

    /// <summary>
    /// The creator's identity key (SPKI bytes).
    /// </summary>
    public byte[] CreatorIdentityKey { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Serialized list of initial member identity keys (SPKI bytes).
    /// </summary>
    public string InitialMembersJson { get; set; } = string.Empty;

    /// <summary>
    /// The group name (nullable).
    /// </summary>
    public string? GroupName { get; set; }

    /// <summary>
    /// UTC timestamp when the invitation was received.
    /// </summary>
    public DateTimeOffset ReceivedAtUtc { get; set; }

    /// <summary>
    /// The status of the invitation.
    /// </summary>
    public PendingGroupInvitationStatus Status { get; set; }
}

/// <summary>
/// The status of a pending group invitation.
/// </summary>
public enum PendingGroupInvitationStatus
{
    /// <summary>
    /// Invitation is pending user acceptance/decline.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Invitation was accepted by the user.
    /// </summary>
    Accepted = 1,

    /// <summary>
    /// Invitation was declined by the user.
    /// </summary>
    Declined = 2
}
