namespace Percolator.Cryptography.GroupLedger;

/// <summary>
/// Aggregate representing a sender key ratchet for a specific group member.
/// Used for O(1) lookup of sender keys during group message decryption.
/// </summary>
public sealed class SenderKeyRatchet
{
    public GroupId Id { get; }
    public CryptoPublicIdentityId AuthorPublicIdentityId { get; }
    public uint KeyId { get; }
    public ChainKey ChainKey { get; }
    public SignaturePublicKey SignatureKey { get; }

    public SenderKeyRatchet(
        GroupId id,
        CryptoPublicIdentityId authorPublicIdentityId,
        uint keyId,
        ChainKey chainKey,
        SignaturePublicKey signatureKey)
    {
        Id = id;
        AuthorPublicIdentityId = authorPublicIdentityId;
        KeyId = keyId;
        ChainKey = chainKey;
        SignatureKey = signatureKey;
    }
}
