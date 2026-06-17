namespace Percolator.Infrastructure.Chat.Persistence;

/// <summary>
/// Represents a blinded routing entry in the Relay's routing table.
/// Maps a conversation to a destination public key hash (wire-safe routing token).
/// </summary>
public class RelayBlindedRosterDbo
{
    /// <summary>
    /// The conversation ID (foreign key to RelayGroupStateDbo).
    /// </summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// The destination public key hash (wire-safe routing token for fan-out).
    /// </summary>
    public byte[] DestinationPkhBytes { get; set; } = null!;
}
