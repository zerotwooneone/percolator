using System.Security.Cryptography;
using Percolator.SourceGenerators;

namespace Percolator.Identity;

/// <summary>
/// Represents the SHA-256 hash of an identity public key (PKH).
/// This type is immutable and enforces the invariant that the value is exactly 32 bytes.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record IdentityPublicKeyHash
{
    /// <summary>
    /// Creates an IdentityPublicKeyHash from a public key SPKI by computing its SHA-256 hash.
    /// </summary>
    /// <param name="spki">The public key SPKI bytes.</param>
    /// <returns>A new IdentityPublicKeyHash instance representing the hash of the SPKI.</returns>
    /// <exception cref="ArgumentNullException">Thrown when spki is null.</exception>
    public static IdentityPublicKeyHash FromSpki(byte[] spki)
    {
        if (spki is null)
            throw new ArgumentNullException(nameof(spki));

        var hash = SHA256.HashData(spki);
        return FromBytesOwned(hash);
    }
}
