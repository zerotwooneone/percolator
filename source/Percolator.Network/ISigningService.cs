using System.Security.Cryptography;

namespace Percolator.Network;

/// <summary>
/// Provides an abstraction for cryptographic operations required for peer discovery,
/// using expressive DDD value types.
/// </summary>
public interface ISigningService
{
    /// <summary>
    /// Gets the active public key of the current node.
    /// </summary>
    PublicKey GetActivePublicKey();

    /// <summary>
    /// Gets the unique and stable hash of the active public key.
    /// </summary>
    PublicKeyHash GetActivePublicKeyHash();

    /// <summary>
    /// Signs the given payload using the active private key.
    /// </summary>
    Signature Sign(Payload payload);

    /// <summary>
    /// Verifies a signature against the provided payload and public key.
    /// </summary>
    bool Verify(Payload payload, Signature signature, PublicKey publicKey);

    /// <summary>
    /// Computes the hash of a given public key to serve as a unique identifier.
    /// </summary>
    PublicKeyHash GetHash(PublicKey publicKey);
}
