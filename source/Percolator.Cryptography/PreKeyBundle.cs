namespace Percolator.Cryptography;

/// <summary>
/// Represents a bundle of public keys published by a user to a server.
/// This bundle allows other users to establish a secure session with the publisher via the X3DH (Extended Triple Diffie-Hellman) protocol.
/// </summary>
public record PreKeyBundle
{
    /// <summary>
    /// The long-term public identity key used for signing.
    /// This key's corresponding private key is used to sign the <see cref="SignedPreKey"/>, proving its authenticity.
    /// This key is static and lasts for the lifetime of the user's identity.
    /// </summary>
    public RatchetIdentityKey IdentitySigningKey { get; }
    /// <summary>
    /// The long-term public key used for key agreement (e.g., ECDH).
    /// It is one of the core components used to derive the final shared secret during the X3DH handshake.
    /// This key is static and lasts for the lifetime of the user's identity.
    /// </summary>
    public RatchetAgreementKey IdentityAgreementKey { get; }
    /// <summary>
    /// The signature over the public part of the <see cref="SignedPreKey"/>.
    /// It is created using the private key corresponding to the <see cref="IdentitySigningKey"/>.
    /// The initiator of a handshake verifies this signature to ensure the SignedPreKey is authentic.
    /// Its lifetime is tied to the <see cref="SignedPreKey"/>.
    /// </summary>
    public Signature SignedPreKeySignature { get; }
    /// <summary>
    /// A medium-term public key used for key agreement, signed by the <see cref="IdentitySigningKey"/>.
    /// It is periodically rotated (e.g., weekly) to provide forward secrecy.
    /// If an adversary compromises the long-term identity key, past messages remain secure if the signed pre-key has been rotated.
    /// </summary>
    public PreKey SignedPreKey { get; }
    /// <summary>
    /// A single-use, ephemeral public key for key agreement.
    /// A server stores a batch of these keys for a user. When an initiator starts a handshake, they fetch and use one key from the bundle.
    /// Once used, the key is discarded by the server. This provides strong forward secrecy and deniability.
    /// This property can be null if no one-time pre-key is available or used.
    /// </summary>
    public OneTimeKey? OneTimePreKey { get; }

    public PreKeyBundle(
        byte[] identitySigningKey,
        byte[] identityAgreementKey,
        Signature signedPreKeySignature,
        byte[] signedPreKey,
        byte[]? oneTimePreKey)
    {
        IdentitySigningKey = new RatchetIdentityKey(identitySigningKey);
        IdentityAgreementKey = new RatchetAgreementKey(identityAgreementKey);
        SignedPreKeySignature = signedPreKeySignature;
        SignedPreKey = new PreKey(signedPreKey);
        OneTimePreKey = oneTimePreKey is not null ? new OneTimeKey(oneTimePreKey) : null;
    }
}
