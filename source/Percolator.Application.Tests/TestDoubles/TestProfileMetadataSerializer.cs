using System.Text;
using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class TestProfileMetadataSerializer : IProfileMetadataSerializer
{
    public byte[] Serialize(ProfileMetadata metadata)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        writer.Write(metadata.Revision);
        writer.Write(metadata.DisplayName ?? string.Empty);
        writer.Write(metadata.StatusBio ?? string.Empty);
        writer.Write(metadata.AvatarBytes?.Length ?? 0);
        if (metadata.AvatarBytes is { Length: > 0 })
        {
            writer.Write(metadata.AvatarBytes);
        }

        if (metadata.AvatarReference != null)
        {
            writer.Write(true);
            writer.Write(metadata.AvatarReference.Id.HexDigest);
            writer.Write(metadata.AvatarReference.MimeType);
            writer.Write(metadata.AvatarReference.PlaintextSizeBytes);
            writer.Write(metadata.AvatarReference.CiphertextSizeBytes);
            writer.Write((int)metadata.AvatarReference.StorageMode);
        }
        else
        {
            writer.Write(false);
        }

        writer.Flush();
        return ms.ToArray();
    }

    public DomainResult<ProfileMetadata> Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return DomainResult<ProfileMetadata>.Failure(new DomainError("EMPTY_PAYLOAD", "Payload is empty."));
        }

        using var ms = new MemoryStream(data.ToArray());
        using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint revision = reader.ReadUInt32();
        string displayName = reader.ReadString();
        string statusBio = reader.ReadString();
        int avatarLength = reader.ReadInt32();
        byte[]? avatarBytes = avatarLength > 0 ? reader.ReadBytes(avatarLength) : null;

        BlobReference? avatarRef = null;
        if (ms.Position < ms.Length && reader.ReadBoolean())
        {
            string hexId = reader.ReadString();
            string mimeType = reader.ReadString();
            long plainSize = reader.ReadInt64();
            long cipherSize = reader.ReadInt64();
            int storageMode = reader.ReadInt32();

            avatarRef = new BlobReference
            {
                Id = new BlobId(hexId),
                CiphertextSha256 = new byte[32],
                EncryptionKey = new byte[32],
                BaseNonce = new byte[12],
                PlaintextSizeBytes = plainSize,
                CiphertextSizeBytes = cipherSize,
                MimeType = mimeType,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
                StorageMode = (BlobStorageMode)storageMode
            };
        }

        return DomainResult<ProfileMetadata>.Success(new ProfileMetadata(
            displayName,
            avatarBytes,
            string.IsNullOrEmpty(statusBio) ? null : statusBio,
            revision,
            avatarRef));
    }
}
