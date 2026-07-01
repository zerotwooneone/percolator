using Percolator.Identity;

namespace Percolator.Infrastructure.Chat.Persistence;

/// <summary>
/// Represents a member's role and membership status in a group conversation.
/// </summary>
public class GroupMemberDbo
{
    /// <summary>
    /// The conversation ID (foreign key).
    /// </summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// The universal public identity ID (UUID).
    /// </summary>
    public Percolator.Chat.GroupLedger.PublicIdentityId PublicIdentityId { get; set; }

    /// <summary>
    /// The local peer ID (null for local self identities).
    /// </summary>
    public PeerId? PeerId { get; set; }

    /// <summary>
    /// The local self ID (null for remote peers).
    /// </summary>
    public uint? SelfId { get; set; }

    /// <summary>
    /// The member's role in the group.
    /// </summary>
    public GroupMemberRole Role { get; set; }

    /// <summary>
    /// UTC timestamp when the member joined the group.
    /// </summary>
    public DateTimeOffset JoinedAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp when the member was removed (null if still a member).
    /// </summary>
    public DateTimeOffset? RemovedAtUtc { get; set; }
}

/// <summary>
/// The role of a member in a group conversation.
/// </summary>
public enum GroupMemberRole
{
    /// <summary>
    /// Regular member with no administrative privileges.
    /// </summary>
    Member = 0,

    /// <summary>
    /// Admin with privileges to add/remove members and change group settings.
    /// </summary>
    Admin = 1
}
