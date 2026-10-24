using System.Buffers.Binary;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Profiles;

public sealed class ProfileManager : IProfileManager
{
    public const int PaddedBlockSizeBytes = 1024;
    public const int CiphertextPackageSizeBytes = 1040; // 1024 padded payload + 16 AEAD tag

    private readonly IProfileMetadataSerializer _serializer;

    public ProfileManager(IProfileMetadataSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public DomainResult<ProfileCiphertextPackage> EncryptProfile(
        ProfileMetadata metadata,
        ProfileKey profileKey,
        ICryptoEngine engine)
    {
        ArgumentNullException.ThrowIfNull(metadata.DisplayName);
        ArgumentNullException.ThrowIfNull(profileKey);
        ArgumentNullException.ThrowIfNull(engine);

        var plaintext = _serializer.Serialize(metadata);
        if (plaintext.Length >= PaddedBlockSizeBytes)
        {
            return DomainResult<ProfileCiphertextPackage>.Failure(
                new DomainError("PROFILE_METADATA_TOO_LARGE", $"Profile metadata ({plaintext.Length} bytes) exceeds maximum padded block capacity of {PaddedBlockSizeBytes - 1} bytes."));
        }

        // ISO/IEC 7816-4 padding to exactly 1024 bytes: 0x80 delimiter followed by 0x00 bytes
        var padded = new byte[PaddedBlockSizeBytes];
        plaintext.CopyTo(padded, 0);
        padded[plaintext.Length] = 0x80;

        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        // Bind 4-byte big-endian revision into AEAD Associated Data (AD) for tamper/rollback resistance
        Span<byte> ad = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(ad, metadata.Revision);

        var ciphertext = engine.EncryptAesGcm(profileKey.Span, nonce, padded, ad);
        return DomainResult<ProfileCiphertextPackage>.Success(new ProfileCiphertextPackage(nonce, ciphertext));
    }

    public DomainResult<ProfileMetadata> DecryptProfile(
        ProfileCiphertextPackage package,
        ProfileKey profileKey,
        ICryptoEngine engine,
        uint? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(package.Nonce);
        ArgumentNullException.ThrowIfNull(package.Ciphertext);
        ArgumentNullException.ThrowIfNull(profileKey);
        ArgumentNullException.ThrowIfNull(engine);

        if (package.Ciphertext.Length != CiphertextPackageSizeBytes)
        {
            return DomainResult<ProfileMetadata>.Failure(
                new DomainError("INVALID_PACKAGE_SIZE", $"Profile package ciphertext must be exactly {CiphertextPackageSizeBytes} bytes."));
        }

        if (!expectedRevision.HasValue)
        {
            return DomainResult<ProfileMetadata>.Failure(
                new DomainError("MISSING_EXPECTED_REVISION", "Decryption requires an explicit expected revision to verify AEAD associated data."));
        }

        // Associated Data strictly binds big-endian revision to prevent rollback/tampering
        Span<byte> ad = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(ad, expectedRevision.Value);

        byte[] plaintext;
        try
        {
            plaintext = engine.DecryptAesGcm(profileKey.Span, package.Nonce, package.Ciphertext, ad);
        }
        catch (Exception ex)
        {
            return DomainResult<ProfileMetadata>.Failure(new DomainError("PROFILE_DECRYPT_FAILED", ex.Message));
        }

        // Remove ISO/IEC 7816-4 padding
        int i = plaintext.Length - 1;
        while (i >= 0 && plaintext[i] == 0x00)
        {
            i--;
        }

        if (i < 0 || plaintext[i] != 0x80)
        {
            return DomainResult<ProfileMetadata>.Failure(new DomainError("INVALID_PADDING", "Invalid ISO/IEC 7816-4 profile padding."));
        }

        var unpaddedSpan = plaintext.AsSpan(0, i);
        return _serializer.Deserialize(unpaddedSpan);
    }
}
