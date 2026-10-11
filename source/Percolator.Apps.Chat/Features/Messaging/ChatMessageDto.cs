using Percolator.Apps.Chat.Features.Media;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record ChatMessageDto(
    PayloadId Id,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    ulong TimestampUnixMs,
    string Content,
    IReadOnlyList<MentionDto> Mentions,
    ReplyContextDto? ReplyContext,
    LinkPreviewDto? LinkPreview,
    IReadOnlyList<MediaAttachmentDto> Attachments,
    int? ExpireAfterSeconds,
    ulong? EditedAtUnixMs);
