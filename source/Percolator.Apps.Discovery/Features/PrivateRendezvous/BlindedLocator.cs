using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.PrivateRendezvous;

/// <summary>
/// Privacy-preserving deterministic rendezvous token derived by mutual contacts:
/// HMAC-SHA256(ContactSharedSecret, EpochDay || PublicIdentityId).
/// Invariants:
/// - Exact 32 bytes (64 hex characters).
/// - Unwrapping is explicit via .Value.
/// - Zero implicit/explicit operators.
/// </summary>
public readonly record struct BlindedLocator : IEquatable<BlindedLocator>
{
    public const int ByteLength = 32;

    public string Value { get; }

    private BlindedLocator(string value)
    {
        Value = value;
    }

    public static DomainResult<BlindedLocator> Create(string hexValue)
    {
        if (string.IsNullOrWhiteSpace(hexValue))
        {
            return DomainResult<BlindedLocator>.Failure(new DomainError(
                "INVALID_LOCATOR", "BlindedLocator hex string cannot be empty or whitespace."));
        }

        var trimmed = hexValue.Trim();
        if (trimmed.Length != ByteLength * 2)
        {
            return DomainResult<BlindedLocator>.Failure(new DomainError(
                "INVALID_LOCATOR_LENGTH", $"BlindedLocator hex string must be exactly {ByteLength * 2} characters (32 bytes)."));
        }

        // Validate hex encoding
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return DomainResult<BlindedLocator>.Failure(new DomainError(
                    "INVALID_LOCATOR_FORMAT", "BlindedLocator contains invalid non-hex characters."));
            }
        }

        return DomainResult<BlindedLocator>.Success(new BlindedLocator(trimmed.ToLowerInvariant()));
    }

    public static BlindedLocator Derive(
        PublicIdentityId peerId,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch epoch)
    {
        if (!peerId.IsValid)
        {
            throw new ArgumentException("PeerId must be valid and non-empty.", nameof(peerId));
        }

        if (sharedSecret.Length < 16)
        {
            throw new ArgumentException("Shared secret must be at least 16 bytes.", nameof(sharedSecret));
        }

        // Message to MAC: [8 bytes epoch] + [16 bytes Guid]
        Span<byte> message = stackalloc byte[8 + 16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message[..8], epoch.Value);
        peerId.TryWriteBytes(message[8..]);

        Span<byte> hash = stackalloc byte[ByteLength];
        HMACSHA256.HashData(sharedSecret, message, hash);

        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return new BlindedLocator(hex);
    }

    public override string ToString() => Value ?? string.Empty;
}
