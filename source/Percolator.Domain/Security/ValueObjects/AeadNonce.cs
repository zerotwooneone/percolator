using System.Buffers.Binary;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// Represents a 96-bit (12-byte) AES-256-GCM nonce derived deterministically via Signal-style
/// HKDF-SHA256 expansion of a single-use ratchet <see cref="MessageKey"/>.
/// </summary>
[ByteArray(length: 12)]
public sealed partial record AeadNonce
{
    public const int NonceLength = 12;
    public const int CipherKeyLength = 32;
    public const int ExpandedMaterialLength = CipherKeyLength + NonceLength; // 44 bytes: [0..32] CipherKey, [32..44] Nonce

    private static ReadOnlySpan<byte> DomainSeparationInfo => "PercolatorMessageKeys-v1"u8;

    /// <summary>
    /// Expands a 32-byte single-use <paramref name="messageKeySeed"/> via HKDF-SHA256 into a 44-byte buffer:
    /// bytes [0..32] = 32-byte AES-256 CipherKey, bytes [32..44] = 12-byte AES-GCM Nonce.
    /// </summary>
    public static void ExpandMessageKeyMaterial(
        ReadOnlySpan<byte> messageKeySeed,
        uint counterOrIteration,
        Span<byte> destination44Bytes)
    {
        if (messageKeySeed.Length != 32)
        {
            throw new ArgumentException("Message key seed must be 32 bytes.", nameof(messageKeySeed));
        }

        if (destination44Bytes.Length < ExpandedMaterialLength)
        {
            throw new ArgumentException(
                $"Expanded material destination must be at least {ExpandedMaterialLength} bytes.",
                nameof(destination44Bytes));
        }

        Span<byte> salt = stackalloc byte[32];
        salt.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(salt, counterOrIteration);

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            messageKeySeed,
            destination44Bytes[..ExpandedMaterialLength],
            salt,
            DomainSeparationInfo);
    }

    /// <summary>
    /// Derives the deterministic 12-byte AES-GCM nonce from a single-use <see cref="MessageKey"/>
    /// and its ratchet message counter or group sender key iteration.
    /// </summary>
    public static AeadNonce DeriveFromMessageKey(MessageKey messageKey, uint counterOrIteration = 0)
    {
        ArgumentNullException.ThrowIfNull(messageKey);

        Span<byte> expanded = stackalloc byte[ExpandedMaterialLength];
        try
        {
            ExpandMessageKeyMaterial(messageKey.Span, counterOrIteration, expanded);
            return FromSpan(expanded.Slice(CipherKeyLength, NonceLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expanded);
        }
    }

    /// <summary>
    /// Writes the deterministic 12-byte AES-GCM nonce for the given single-use message key span
    /// and ratchet counter/iteration into <paramref name="destination"/>.
    /// </summary>
    public static void WriteFromMessageKey(
        ReadOnlySpan<byte> messageKey,
        uint counterOrIteration,
        Span<byte> destination)
    {
        if (destination.Length < NonceLength)
        {
            throw new ArgumentException($"Nonce destination must be at least {NonceLength} bytes.", nameof(destination));
        }

        Span<byte> expanded = stackalloc byte[ExpandedMaterialLength];
        try
        {
            ExpandMessageKeyMaterial(messageKey, counterOrIteration, expanded);
            expanded.Slice(CipherKeyLength, NonceLength).CopyTo(destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expanded);
        }
    }
}

/// <summary>
/// Holds the Signal-style HKDF-expanded 32-byte AES-256 cipher key and 12-byte AES-GCM nonce
/// derived from a single-use <see cref="MessageKey"/> seed. Deterministically zeroizes key material on disposal.
/// </summary>
public sealed class ExpandedMessageKeys : ISensitiveSecret
{
    private readonly byte[] _material;
    public bool IsZeroized { get; private set; }

    internal ExpandedMessageKeys(byte[] material44Bytes)
    {
        _material = material44Bytes;
    }

    public ReadOnlySpan<byte> CipherKey => _material.AsSpan(0, AeadNonce.CipherKeyLength);
    public ReadOnlySpan<byte> Nonce => _material.AsSpan(AeadNonce.CipherKeyLength, AeadNonce.NonceLength);

    public AeadNonce ToAeadNonce() => AeadNonce.FromSpan(Nonce);

    public void Zeroize()
    {
        if (IsZeroized)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_material);
        IsZeroized = true;
    }

    public void Dispose()
    {
        Zeroize();
    }
}
