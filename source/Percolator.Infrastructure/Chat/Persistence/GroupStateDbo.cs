using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.Infrastructure.Chat.Persistence;

/// <summary>
/// Represents the state of a group conversation.
/// Stores the epoch (key rotation version), group name, and public params.
/// </summary>
public class GroupStateDbo
{
    /// <summary>
    /// The conversation ID (primary key).
    /// </summary>
    public ConversationId ConversationId { get; set; }

    /// <summary>
    /// The current epoch (monotonic counter for key rotation).
    /// </summary>
    public int Epoch { get; set; }

    /// <summary>
    /// The group name (plaintext, protected by database encryption).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The relay group public parameters (32 bytes).
    /// </summary>
    public RelayGroupPublicParamsBytes PublicParams { get; set; }

    /// <summary>
    /// The local peer ID of the relay for this group.
    /// </summary>
    public PeerId RelayPeerId { get; set; }

    /// <summary>
    /// UTC timestamp when the group was created.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp when the group state was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
