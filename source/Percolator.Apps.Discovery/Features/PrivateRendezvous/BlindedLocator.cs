using System.Buffers.Binary;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.PrivateRendezvous;

/// <summary>
/// Privacy-preserving deterministic 32-byte rendezvous token derived by mutual contacts:
/// HMAC-SHA256(ContactSharedSecret, "percolator-discovery-rendezvous-v1" || EpochDay || PublicIdentityId).
/// Invariants:
/// - Exact 32 bytes (64 hex characters).
/// - Zero-allocation stack hashing during derivation.
/// - Zero implicit/explicit operators.
/// </summary>
public readonly struct BlindedLocator : IEquatable<BlindedLocator>
{
    public const int ByteLength = 32;

    private static ReadOnlySpan<byte> DomainSeparationPrefix => "percolator-discovery-rendezvous-v1"u8;

    private readonly byte[]? _bytes;
    private readonly string? _hex;

    public bool IsValid => _bytes is { Length: ByteLength };

    public ReadOnlySpan<byte> Span => _bytes is { Length: ByteLength } ? _bytes : ReadOnlySpan<byte>.Empty;

    public ReadOnlyMemory<byte> Bytes => _bytes is { Length: ByteLength } ? _bytes : ReadOnlyMemory<byte>.Empty;

    public string Value => _hex ?? string.Empty;

    private BlindedLocator(byte[] bytes, string hex)
    {
        _bytes = bytes;
        _hex = hex;
    }

    public static DomainResult<BlindedLocator> FromSpan(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
        {
            return DomainResult<BlindedLocator>.Failure(new DomainError(
                "INVALID_LOCATOR_LENGTH", $"BlindedLocator must be exactly {ByteLength} bytes."));
        }

        byte[] copy = bytes.ToArray();
        string hex = Convert.ToHexString(copy).ToLowerInvariant();
        return DomainResult<BlindedLocator>.Success(new BlindedLocator(copy, hex));
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

        string normalizedHex = trimmed.ToLowerInvariant();
        byte[] rawBytes = Convert.FromHexString(normalizedHex);
        return DomainResult<BlindedLocator>.Success(new BlindedLocator(rawBytes, normalizedHex));
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

        // Domain-separated message: ["percolator-discovery-rendezvous-v1" (34B)] + [8B epoch] + [16B Guid] = 58 bytes
        ReadOnlySpan<byte> prefix = DomainSeparationPrefix;
        Span<byte> message = stackalloc byte[34 + 8 + 16];
        prefix.CopyTo(message);
        BinaryPrimitives.WriteInt64BigEndian(message.Slice(34, 8), epoch.Value);
        peerId.TryWriteBytes(message.Slice(34 + 8, 16));

        byte[] hash = new byte[ByteLength];
        HMACSHA256.HashData(sharedSecret, message, hash);

        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return new BlindedLocator(hash, hex);
    }

    /// <summary>
    /// Derives the 3-epoch tolerance window [epoch - 1, epoch, epoch + 1] (or [0, 1] at epoch 0)
    /// to prevent rendezvous misses across UTC midnight clock skew.
    /// </summary>
    public static IReadOnlyList<BlindedLocator> DeriveWindow(
        PublicIdentityId peerId,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch currentEpoch)
    {
        if (currentEpoch.Value == 0)
        {
            return new[]
            {
                Derive(peerId, sharedSecret, new DiscoveryEpoch(0)),
                Derive(peerId, sharedSecret, new DiscoveryEpoch(1))
            };
        }

        return new[]
        {
            Derive(peerId, sharedSecret, new DiscoveryEpoch(currentEpoch.Value - 1)),
            Derive(peerId, sharedSecret, currentEpoch),
            Derive(peerId, sharedSecret, new DiscoveryEpoch(currentEpoch.Value + 1))
        };
    }

    public bool Equals(BlindedLocator other)
    {
        if (_bytes is null && other._bytes is null) return true;
        if (_bytes is null || other._bytes is null) return false;
        return CryptographicOperations.FixedTimeEquals(_bytes, other._bytes);
    }

    public override bool Equals(object? obj) => obj is BlindedLocator other && Equals(other);

    public override int GetHashCode() =>
        _hex is not null ? StringComparer.Ordinal.GetHashCode(_hex) : 0;

    public static bool operator ==(BlindedLocator left, BlindedLocator right) => left.Equals(right);
    public static bool operator !=(BlindedLocator left, BlindedLocator right) => !left.Equals(right);

    public override string ToString() => Value;
}
