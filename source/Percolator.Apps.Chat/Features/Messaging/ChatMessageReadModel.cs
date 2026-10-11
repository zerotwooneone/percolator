using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record ChatMessageReadModel(
    PayloadId Id,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    ulong TimestampUnixMs,
    string Content,
    IReadOnlyList<MentionDto> Mentions,
    ReplyContextDto? ReplyContext,
    LinkPreviewDto? LinkPreview,
    IReadOnlyList<MediaAttachmentDto> Attachments,
    ulong? EditedAtUnixMs,
    bool IsTombstone,
    IReadOnlyList<ReactionGroupReadModel> Reactions);
