using Google.Protobuf;
using Percolator.Domain.Common;
using Percolator.Infrastructure2.Protos;
using Percolator.PluginSdk;

namespace Percolator.Infrastructure2.Serialization;

public sealed class ProtobufProfileSerializer : IProfileMetadataSerializer
{
    public byte[] Serialize(ProfileMetadata metadata)
    {
        var proto = new ProfileMetadataPayloadProto
        {
            Revision = metadata.Revision,
            DisplayName = metadata.DisplayName ?? string.Empty,
            StatusBio = metadata.StatusBio ?? string.Empty,
            AvatarBytes = metadata.AvatarBytes is { Length: > 0 }
                ? ByteString.CopyFrom(metadata.AvatarBytes)
                : ByteString.Empty
        };

        if (metadata.AvatarReference != null)
        {
            proto.AvatarReference = ToProto(metadata.AvatarReference);
        }

        return proto.ToByteArray();
    }

    public DomainResult<ProfileMetadata> Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return DomainResult<ProfileMetadata>.Failure(
                new DomainError("EMPTY_DATA", "Cannot deserialize empty profile metadata."));
        }

        try
        {
            var proto = ProfileMetadataPayloadProto.Parser.ParseFrom(data);
            BlobReference? avatarRef = proto.AvatarReference != null ? FromProto(proto.AvatarReference) : null;
            byte[]? avatarBytes = proto.AvatarBytes is { Length: > 0 } ? proto.AvatarBytes.ToByteArray() : null;

            return DomainResult<ProfileMetadata>.Success(new ProfileMetadata(
                proto.DisplayName,
                avatarBytes,
                string.IsNullOrEmpty(proto.StatusBio) ? null : proto.StatusBio,
                proto.Revision,
                avatarRef));
        }
        catch (Exception ex)
        {
            return DomainResult<ProfileMetadata>.Failure(
                new DomainError("DESERIALIZATION_FAILED", ex.Message));
        }
    }

    private static BlobReferenceProto ToProto(BlobReference b)
    {
        var proto = new BlobReferenceProto
        {
            Id = b.Id.HexDigest,
            CiphertextSha256 = ByteString.CopyFrom(b.CiphertextSha256),
            EncryptionKey = ByteString.CopyFrom(b.EncryptionKey),
            BaseNonce = ByteString.CopyFrom(b.BaseNonce),
            PlaintextSizeBytes = b.PlaintextSizeBytes,
            CiphertextSizeBytes = b.CiphertextSizeBytes,
            MimeType = b.MimeType ?? string.Empty,
            ExpiresAtUnixSeconds = b.ExpiresAtUtc.ToUnixTimeSeconds(),
            StorageMode = (uint)b.StorageMode,
            RelayEndpointUri = b.RelayEndpointUri ?? string.Empty,
            BlurHash = b.BlurHash ?? string.Empty,
            InlineThumbnail = b.InlineThumbnail != null ? ByteString.CopyFrom(b.InlineThumbnail) : ByteString.Empty,
            Waveform = b.Waveform != null ? ByteString.CopyFrom(b.Waveform) : ByteString.Empty,
            Width = b.Width ?? 0,
            Height = b.Height ?? 0,
            DurationSeconds = b.DurationSeconds ?? 0
        };

        return proto;
    }

    private static BlobReference FromProto(BlobReferenceProto p)
    {
        return new BlobReference
        {
            Id = new BlobId(p.Id),
            CiphertextSha256 = p.CiphertextSha256.ToByteArray(),
            EncryptionKey = p.EncryptionKey.ToByteArray(),
            BaseNonce = p.BaseNonce.ToByteArray(),
            PlaintextSizeBytes = p.PlaintextSizeBytes,
            CiphertextSizeBytes = p.CiphertextSizeBytes,
            MimeType = p.MimeType,
            ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(p.ExpiresAtUnixSeconds),
            StorageMode = (BlobStorageMode)p.StorageMode,
            RelayEndpointUri = string.IsNullOrEmpty(p.RelayEndpointUri) ? null : p.RelayEndpointUri,
            BlurHash = string.IsNullOrEmpty(p.BlurHash) ? null : p.BlurHash,
            InlineThumbnail = p.InlineThumbnail.IsEmpty ? null : p.InlineThumbnail.ToByteArray(),
            Waveform = p.Waveform.IsEmpty ? null : p.Waveform.ToByteArray(),
            Width = p.Width > 0 ? p.Width : null,
            Height = p.Height > 0 ? p.Height : null,
            DurationSeconds = p.DurationSeconds > 0 ? p.DurationSeconds : null
        };
    }
}
