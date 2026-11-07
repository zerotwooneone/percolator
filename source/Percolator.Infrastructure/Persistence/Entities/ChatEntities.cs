namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class ChatMessageEntity
{
    public Guid Id { get; set; }
    public Guid ChannelId { get; set; }
    public byte[] AuthorId { get; set; } = Array.Empty<byte>();
    public ulong TimestampUnixMs { get; set; }
    public string Content { get; set; } = string.Empty;
    public Guid? ReplyParentId { get; set; }
    public byte[]? ReplyAuthorId { get; set; }

    // Link Preview normalized columns
    public string? LinkPreviewTitle { get; set; }
    public string? LinkPreviewDescription { get; set; }
    public string? LinkPreviewCanonicalUrl { get; set; }
    public string? LinkPreviewThumbnailBlobId { get; set; }
    public byte[]? LinkPreviewThumbnailSha256 { get; set; }
    public byte[]? LinkPreviewThumbnailKey { get; set; }
    public byte[]? LinkPreviewThumbnailNonce { get; set; }
    public int? LinkPreviewThumbnailStorageMode { get; set; }
    public string? LinkPreviewThumbnailRelayUri { get; set; }
    public long? LinkPreviewThumbnailCiphertextSize { get; set; }
    public string? LinkPreviewThumbnailMimeType { get; set; }

    public int? ExpireAfterSeconds { get; set; }
    public ulong? ExpiresAtUnixMs { get; set; }
    public ulong? DeliveredAtUnixMs { get; set; }
    public ulong? ReadAtUnixMs { get; set; }
    public ulong? EditedAtUnixMs { get; set; }
    public bool IsTombstone { get; set; }
    public ulong? DeletedAtUnixMs { get; set; }
    public byte[]? DeletedBy { get; set; }
}

public sealed class ChatMessageMentionEntity
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public byte[] IdentityId { get; set; } = Array.Empty<byte>();
    public int StartOffset { get; set; }
    public int Length { get; set; }
}

public sealed class ChatReactionEntity
{
    public Guid MessageId { get; set; }
    public byte[] ReactorId { get; set; } = Array.Empty<byte>();
    public string Emoji { get; set; } = string.Empty;
    public ulong TimestampUnixMs { get; set; }
}

public sealed class ChatAttachmentEntity
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public string BlobId { get; set; } = string.Empty;
    public byte[] BlobSha256 { get; set; } = Array.Empty<byte>();
    public string? FileName { get; set; }
    public long FileSizeBytes { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public byte[] EncryptionKey { get; set; } = Array.Empty<byte>();
    public byte[] BaseNonce { get; set; } = Array.Empty<byte>();
    public int StorageMode { get; set; }
    public string? RelayEndpointUri { get; set; }
    public long CiphertextSizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public byte[]? Waveform { get; set; }
    public byte[]? InlineBytes { get; set; }
}

public sealed class ChatGroupInvitationEntity
{
    public string Id { get; set; } = string.Empty;
    public Guid TargetGroupChannelId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public byte[] InviterId { get; set; } = Array.Empty<byte>();
    public string? RelayEndpoint { get; set; }
    public ulong ExpiresAtUnixMs { get; set; }
    public int Status { get; set; }
    public ulong? RespondedAtUnixMs { get; set; }
    public byte[]? InitialMembersBytes { get; set; }
}

public sealed class ChatUnknownGroupMessageEntity
{
    public Guid Id { get; set; }
    public Guid ChannelId { get; set; }
    public byte[] AuthorId { get; set; } = Array.Empty<byte>();
    public byte[] EncryptedPayload { get; set; } = Array.Empty<byte>();
    public ulong ReceivedAtUnixMs { get; set; }
    public ulong ExpiresAtUnixMs { get; set; }
}
