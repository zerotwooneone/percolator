using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;

namespace Percolator.Application2.Profiles;

public readonly record struct ProfileMetadata(
    string DisplayName,
    byte[]? AvatarBytes,
    string? StatusBio,
    uint Revision);

public readonly record struct ProfileCiphertextPackage(
    byte[] Nonce,
    byte[] Ciphertext);

public sealed class ProfileKey : IDisposable
{
    private readonly byte[] _keyBytes;
    public ReadOnlySpan<byte> Span => _keyBytes;

    public ProfileKey(byte[] keyBytes)
    {
        if (keyBytes.Length != 32)
        {
            throw new ArgumentException("Profile key must be 32 bytes.", nameof(keyBytes));
        }

        _keyBytes = (byte[])keyBytes.Clone();
    }

    public static ProfileKey Generate()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return new ProfileKey(bytes);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_keyBytes);
    }
}

public interface IProfileManager
{
    DomainResult<ProfileCiphertextPackage> EncryptProfile(
        ProfileMetadata metadata,
        ProfileKey profileKey,
        ICryptoEngine engine);

    DomainResult<ProfileMetadata> DecryptProfile(
        ProfileCiphertextPackage package,
        ProfileKey profileKey,
        ICryptoEngine engine);
}

public sealed class ProfileManager : IProfileManager
{
    public DomainResult<ProfileCiphertextPackage> EncryptProfile(
        ProfileMetadata metadata,
        ProfileKey profileKey,
        ICryptoEngine engine)
    {
        ArgumentNullException.ThrowIfNull(metadata.DisplayName);
        ArgumentNullException.ThrowIfNull(profileKey);
        ArgumentNullException.ThrowIfNull(engine);

        // Pack metadata: [4 bytes revision] + [2 bytes name len] + [name bytes] + [bio len] + [bio bytes] + [avatar bytes]
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(metadata.Revision);
        writer.Write(metadata.DisplayName);
        writer.Write(metadata.StatusBio ?? string.Empty);
        writer.Write(metadata.AvatarBytes?.Length ?? 0);
        if (metadata.AvatarBytes is { Length: > 0 })
        {
            writer.Write(metadata.AvatarBytes);
        }
        writer.Flush();

        var plaintext = ms.ToArray();
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = engine.EncryptAesGcm(profileKey.Span, nonce, plaintext, ReadOnlySpan<byte>.Empty);
        return DomainResult<ProfileCiphertextPackage>.Success(new ProfileCiphertextPackage(nonce, ciphertext));
    }

    public DomainResult<ProfileMetadata> DecryptProfile(
        ProfileCiphertextPackage package,
        ProfileKey profileKey,
        ICryptoEngine engine)
    {
        ArgumentNullException.ThrowIfNull(package.Nonce);
        ArgumentNullException.ThrowIfNull(package.Ciphertext);
        ArgumentNullException.ThrowIfNull(profileKey);
        ArgumentNullException.ThrowIfNull(engine);

        byte[] plaintext;
        try
        {
            plaintext = engine.DecryptAesGcm(profileKey.Span, package.Nonce, package.Ciphertext, ReadOnlySpan<byte>.Empty);
        }
        catch (Exception ex)
        {
            return DomainResult<ProfileMetadata>.Failure(new DomainError("PROFILE_DECRYPT_FAILED", ex.Message));
        }

        using var ms = new MemoryStream(plaintext);
        using var reader = new BinaryReader(ms);

        uint revision = reader.ReadUInt32();
        string displayName = reader.ReadString();
        string statusBio = reader.ReadString();
        int avatarLength = reader.ReadInt32();
        byte[]? avatarBytes = avatarLength > 0 ? reader.ReadBytes(avatarLength) : null;

        return DomainResult<ProfileMetadata>.Success(new ProfileMetadata(
            displayName,
            avatarBytes,
            string.IsNullOrEmpty(statusBio) ? null : statusBio,
            revision));
    }
}
