using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// 32-byte authorization token presented when delivering messages into a recipient's mailbox.
/// Implements Signal Sealed Sender delivery authorization: holding the recipient's ProfileKey authorizes
/// message enqueue without revealing the sender's identity.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record DeliveryToken : ISensitiveSecret
{
    public static DeliveryToken DeriveFromProfileKey(IdentityKey ownerPublicKey, ProfileKey profileKey, ICryptoEngine engine)
    {
        ArgumentNullException.ThrowIfNull(ownerPublicKey);
        ArgumentNullException.ThrowIfNull(profileKey);
        ArgumentNullException.ThrowIfNull(engine);

        var bytes = engine.HkdfSha256(
            ownerPublicKey.Span,
            profileKey.Span,
            "PercolatorDeliveryTokenV1"u8,
            32);

        return FromSpan(bytes);
    }

    /// <summary>
    /// Generates a fresh 32-byte cryptographically secure random token.
    /// Used for relay mailbox drain tokens (Signal's account-auth-password analogue), which are
    /// random bearer secrets rather than values derived from the ProfileKey.
    /// </summary>
    public static DeliveryToken GenerateRandom()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return FromSpan(bytes);
    }

    public void Zeroize()
    {
        if (MemoryMarshal.TryGetArray(_value, out var segment) && segment.Array != null)
        {
            CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
        }
    }

    public void Dispose()
    {
        Zeroize();
    }
}
