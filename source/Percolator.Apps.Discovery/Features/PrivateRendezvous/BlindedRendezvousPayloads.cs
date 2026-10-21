using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.PrivateRendezvous;

/// <summary>
/// Value object representing a validated relay routing descriptor for rendezvous.
/// </summary>
public readonly record struct RelayRouteDescriptor
{
    public const int MaxLength = 512;

    public string Value { get; }
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= MaxLength;

    public RelayRouteDescriptor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Relay route descriptor cannot be empty.", nameof(value));
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Relay route descriptor cannot exceed {MaxLength} characters.");
        }

        Value = trimmed;
    }

    public static DomainResult<RelayRouteDescriptor> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DomainResult<RelayRouteDescriptor>.Failure(new DomainError(
                "INVALID_ROUTE", "Relay route descriptor cannot be empty."));
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            return DomainResult<RelayRouteDescriptor>.Failure(new DomainError(
                "ROUTE_TOO_LONG", $"Relay route descriptor cannot exceed {MaxLength} characters."));
        }

        return DomainResult<RelayRouteDescriptor>.Success(new RelayRouteDescriptor(trimmed));
    }

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// AEAD-encrypted rendezvous route descriptor (AES-256-GCM: [12B Nonce || Ciphertext || 16B Tag]).
/// Ensures the hosting relay learns zero routing or endpoint metadata from stored rendezvous entries
/// and prevents third-party poisoning by binding the BlindedLocator as Associated Authenticated Data (AAD).
/// </summary>
public readonly struct EncryptedRendezvousRoute : IEquatable<EncryptedRendezvousRoute>
{
    public const int NonceByteLength = 12;
    public const int TagByteLength = 16;
    public const int MinByteLength = NonceByteLength + 1 + TagByteLength;
    public const int MaxByteLength = NonceByteLength + (RelayRouteDescriptor.MaxLength * 4) + TagByteLength;

    private static ReadOnlySpan<byte> HkdfInfoPrefix => "percolator-rendezvous-route-key-v1"u8;

    private readonly byte[]? _ciphertextWithNonceAndTag;

    public bool IsValid => _ciphertextWithNonceAndTag is { Length: >= MinByteLength and <= MaxByteLength };

    public ReadOnlySpan<byte> Span => _ciphertextWithNonceAndTag ?? ReadOnlySpan<byte>.Empty;

    public ReadOnlyMemory<byte> Bytes => _ciphertextWithNonceAndTag ?? ReadOnlyMemory<byte>.Empty;

    private EncryptedRendezvousRoute(byte[] ciphertextWithNonceAndTag)
    {
        _ciphertextWithNonceAndTag = ciphertextWithNonceAndTag;
    }

    public static DomainResult<EncryptedRendezvousRoute> FromBytes(ReadOnlySpan<byte> rawBytes)
    {
        if (rawBytes.Length is < MinByteLength or > MaxByteLength)
        {
            return DomainResult<EncryptedRendezvousRoute>.Failure(new DomainError(
                "INVALID_ENCRYPTED_ROUTE",
                $"EncryptedRendezvousRoute must be between {MinByteLength} and {MaxByteLength} bytes."));
        }

        return DomainResult<EncryptedRendezvousRoute>.Success(new EncryptedRendezvousRoute(rawBytes.ToArray()));
    }

    public static EncryptedRendezvousRoute Encrypt(
        RelayRouteDescriptor route,
        BlindedLocator locator,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch epoch,
        PublicIdentityId publisherPeerId)
    {
        if (!route.IsValid)
            throw new ArgumentException("RelayRouteDescriptor must be valid.", nameof(route));
        if (!locator.IsValid)
            throw new ArgumentException("BlindedLocator must be valid.", nameof(locator));
        if (sharedSecret.Length < 16)
            throw new ArgumentException("Shared secret must be at least 16 bytes.", nameof(sharedSecret));
        if (!publisherPeerId.IsValid)
            throw new ArgumentException("Publisher PeerId must be valid.", nameof(publisherPeerId));

        Span<byte> aeadKey = stackalloc byte[32];
        DeriveAeadKey(sharedSecret, epoch, publisherPeerId, aeadKey);

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(route.Value);
        byte[] output = new byte[NonceByteLength + plaintextBytes.Length + TagByteLength];

        Span<byte> nonce = output.AsSpan(0, NonceByteLength);
        Span<byte> ciphertext = output.AsSpan(NonceByteLength, plaintextBytes.Length);
        Span<byte> tag = output.AsSpan(NonceByteLength + plaintextBytes.Length, TagByteLength);

        RandomNumberGenerator.Fill(nonce);

        using var aesGcm = new AesGcm(aeadKey, TagByteLength);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, locator.Span);

        CryptographicOperations.ZeroMemory(aeadKey);
        return new EncryptedRendezvousRoute(output);
    }

    public DomainResult<RelayRouteDescriptor> Decrypt(
        BlindedLocator locator,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch epoch,
        PublicIdentityId publisherPeerId)
    {
        if (!IsValid || _ciphertextWithNonceAndTag is null)
        {
            return DomainResult<RelayRouteDescriptor>.Failure(new DomainError(
                "INVALID_ENCRYPTED_ROUTE", "Encrypted rendezvous route is uninitialized or malformed."));
        }

        if (!locator.IsValid || sharedSecret.Length < 16 || !publisherPeerId.IsValid)
        {
            return DomainResult<RelayRouteDescriptor>.Failure(new DomainError(
                "INVALID_DECRYPTION_PARAMETERS", "Locator, shared secret, and publisher identity must be valid."));
        }

        Span<byte> aeadKey = stackalloc byte[32];
        DeriveAeadKey(sharedSecret, epoch, publisherPeerId, aeadKey);

        int ciphertextLen = _ciphertextWithNonceAndTag.Length - NonceByteLength - TagByteLength;
        ReadOnlySpan<byte> nonce = _ciphertextWithNonceAndTag.AsSpan(0, NonceByteLength);
        ReadOnlySpan<byte> ciphertext = _ciphertextWithNonceAndTag.AsSpan(NonceByteLength, ciphertextLen);
        ReadOnlySpan<byte> tag = _ciphertextWithNonceAndTag.AsSpan(NonceByteLength + ciphertextLen, TagByteLength);

        byte[] plaintext = new byte[ciphertextLen];
        try
        {
            using var aesGcm = new AesGcm(aeadKey, TagByteLength);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, locator.Span);
            string routeText = Encoding.UTF8.GetString(plaintext);
            return RelayRouteDescriptor.Create(routeText);
        }
        catch (CryptographicException)
        {
            return DomainResult<RelayRouteDescriptor>.Failure(new DomainError(
                "RENDEZVOUS_DECRYPTION_FAILED",
                "Failed to authenticate and decrypt the rendezvous route descriptor."));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aeadKey);
        }
    }

    private static void DeriveAeadKey(
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch epoch,
        PublicIdentityId publisherPeerId,
        Span<byte> destinationKey)
    {
        Span<byte> salt = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(salt, epoch.Value);

        ReadOnlySpan<byte> prefix = HkdfInfoPrefix;
        Span<byte> info = stackalloc byte[34 + 16];
        prefix.CopyTo(info);
        publisherPeerId.TryWriteBytes(info.Slice(34, 16));

        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, destinationKey, salt, info);
    }

    public bool Equals(EncryptedRendezvousRoute other)
    {
        if (_ciphertextWithNonceAndTag is null && other._ciphertextWithNonceAndTag is null) return true;
        if (_ciphertextWithNonceAndTag is null || other._ciphertextWithNonceAndTag is null) return false;
        return _ciphertextWithNonceAndTag.AsSpan().SequenceEqual(other._ciphertextWithNonceAndTag);
    }

    public override bool Equals(object? obj) => obj is EncryptedRendezvousRoute other && Equals(other);

    public override int GetHashCode() =>
        _ciphertextWithNonceAndTag is { Length: >= 4 }
            ? BitConverter.ToInt32(_ciphertextWithNonceAndTag, 0)
            : 0;
}

public sealed record BlindedRendezvousPublishPayload(
    BlindedLocator Locator,
    EncryptedRendezvousRoute EncryptedRoute);

public sealed record BlindedRendezvousQueryPayload(
    IReadOnlyList<BlindedLocator> Locators);

public sealed record BlindedRendezvousResponsePayload(
    IReadOnlyList<BlindedRendezvousMatch> Matches);

public sealed record BlindedRendezvousMatch
{
    public BlindedLocator Locator { get; }
    public EncryptedRendezvousRoute EncryptedRoute { get; }
    public string RelayRouteDescriptor { get; }

    public BlindedRendezvousMatch(BlindedLocator locator, EncryptedRendezvousRoute encryptedRoute)
    {
        Locator = locator;
        EncryptedRoute = encryptedRoute;
        RelayRouteDescriptor = encryptedRoute.IsValid
            ? Convert.ToBase64String(encryptedRoute.Span)
            : string.Empty;
    }

    public BlindedRendezvousMatch(BlindedLocator locator, string relayRouteDescriptor)
    {
        Locator = locator;
        RelayRouteDescriptor = relayRouteDescriptor ?? string.Empty;
        EncryptedRoute = default;
    }

    public BlindedRendezvousMatch(
        BlindedLocator locator,
        EncryptedRendezvousRoute encryptedRoute,
        string relayRouteDescriptor)
    {
        Locator = locator;
        EncryptedRoute = encryptedRoute;
        RelayRouteDescriptor = relayRouteDescriptor ?? string.Empty;
    }
}
