namespace Percolator.Infrastructure.Chat.Persistence;

/// <summary>
/// Represents the Relay's authoritative encrypted ledger for a group.
/// Stores the group public parameters and the current epoch for optimistic concurrency.
/// </summary>
public class RelayGroupStateDbo
{
    /// <summary>
    /// The conversation ID (primary key).
    /// </summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// The current epoch (monotonic counter for group state transitions).
    /// </summary>
    public uint Epoch { get; set; }

    /// <summary>
    /// The serialized group public parameters (ZK group public key).
    /// </summary>
    public byte[] GroupPublicParams { get; set; } = null!;

    /// <summary>
    /// Concurrency token for optimistic concurrency control.
    /// </summary>
    public int Version { get; set; }
}
