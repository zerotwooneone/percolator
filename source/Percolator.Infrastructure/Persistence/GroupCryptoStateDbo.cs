namespace Percolator.Infrastructure.Persistence;

/// <summary>
/// Stores the encrypted GroupMasterKey for a group conversation.
/// This is the only persisted cryptographic state for Signal Group V2.
/// The GroupMasterKey is the 32-byte root secret from which all derived
/// sub-keys (group_id, blob_key, encryption keypairs) are deterministically rehydrated.
/// </summary>
public class GroupCryptoStateDbo
{
    /// <summary>
    /// Primary key referencing the conversation.
    /// </summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// The 32-byte GroupMasterKey.
    /// </summary>
    public byte[] GroupMasterKeyBytes { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// UTC timestamp when this crypto state was created.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp when this crypto state was last updated (e.g., during key rotation).
    /// </summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
