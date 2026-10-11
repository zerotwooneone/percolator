using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Models.ReadModels;

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
