using System.Security.Cryptography;

namespace Percolator.Identity;

/// <summary>
/// Represents the SHA-256 hash of an identity public key (PKH).
/// This type is immutable and enforces the invariant that the value is exactly 32 bytes.
/// </summary>
public sealed record IdentityPublicKeyHash : IEquatable<IdentityPublicKeyHash>
{
    private readonly ReadOnlyMemory<byte> _value;

    private IdentityPublicKeyHash(ReadOnlyMemory<byte> value)
    {
        _value = value;
    }

    /// <summary>
    /// Creates an IdentityPublicKeyHash from raw bytes, validating that the length is exactly 32 bytes.
    /// </summary>
    /// <param name="bytes">The 32-byte hash value.</param>
    /// <returns>A new IdentityPublicKeyHash instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when bytes is null.</exception>
    /// <exception cref="ArgumentException">Thrown when bytes length is not 32.</exception>
    public static IdentityPublicKeyHash FromBytes(byte[] bytes)
    {
        if (bytes is null)
            throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length != 32)
            throw new ArgumentException("Public key hash must be exactly 32 bytes.", nameof(bytes));

        // Create a defensive copy to ensure immutability
        var copy = new byte[32];
        Buffer.BlockCopy(bytes, 0, copy, 0, 32);
        return new IdentityPublicKeyHash(copy);
    }

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
        return FromBytes(hash);
    }

    /// <summary>
    /// Returns a defensive copy of the hash value as a byte array.
    /// </summary>
    /// <returns>A new byte array containing the 32-byte hash.</returns>
    public byte[] ToArray()
    {
        var copy = new byte[32];
        _value.Span.CopyTo(copy);
        return copy;
    }

    /// <summary>
    /// Returns the hash value as a read-only memory span.
    /// </summary>
    public ReadOnlyMemory<byte> AsReadOnlyMemory() => _value;

    public bool Equals(IdentityPublicKeyHash? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _value.Span.SequenceEqual(other._value.Span);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            var span = _value.Span;
            for (int i = 0; i < span.Length; i++)
            {
                hash = hash * 23 + span[i];
            }
            return hash;
        }
    }
}
