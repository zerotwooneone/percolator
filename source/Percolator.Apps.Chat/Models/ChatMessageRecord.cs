using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Models;

public sealed class ChatMessageRecord
{
    private const ulong MaxEditWindowMs = 24UL * 60 * 60 * 1000; // 24 hours

    public PayloadId Id { get; }
    public ChannelId ChannelId { get; }
    public PublicIdentityId AuthorId { get; }
    public ulong TimestampUnixMs { get; }
    public string Content { get; private set; }
    public IReadOnlyList<MentionDto> Mentions { get; private set; }
    public ReplyContextDto? ReplyContext { get; }
    public LinkPreviewDto? LinkPreview { get; private set; }
    public IReadOnlyList<MediaAttachmentDto> Attachments { get; private set; }
    public int? ExpireAfterSeconds { get; }
    public ulong? ExpiresAtUnixMs { get; }
    public ulong? EditedAtUnixMs { get; private set; }
    public bool IsTombstone { get; private set; }
    public ulong? DeletedAtUnixMs { get; private set; }
    public PublicIdentityId? DeletedBy { get; private set; }

    private readonly List<ReactionUpdateDto> _reactions = new();
    public IReadOnlyList<ReactionUpdateDto> Reactions => _reactions;

    public ChatMessageRecord(
        PayloadId id,
        ChannelId channelId,
        PublicIdentityId authorId,
        ulong timestampUnixMs,
        string content,
        IReadOnlyList<MentionDto>? mentions = null,
        ReplyContextDto? replyContext = null,
        LinkPreviewDto? linkPreview = null,
        IReadOnlyList<MediaAttachmentDto>? attachments = null,
        int? expireAfterSeconds = null,
        ulong? expiresAtUnixMs = null,
        ulong? editedAtUnixMs = null,
        bool isTombstone = false,
        ulong? deletedAtUnixMs = null,
        PublicIdentityId? deletedBy = null,
        IEnumerable<ReactionUpdateDto>? reactions = null)
    {
        Id = id;
        ChannelId = channelId;
        AuthorId = authorId;
        TimestampUnixMs = timestampUnixMs;
        Content = content;
        Mentions = mentions ?? Array.Empty<MentionDto>();
        ReplyContext = replyContext;
        LinkPreview = linkPreview;
        Attachments = attachments ?? Array.Empty<MediaAttachmentDto>();
        ExpireAfterSeconds = expireAfterSeconds;
        ExpiresAtUnixMs = expiresAtUnixMs;
        EditedAtUnixMs = editedAtUnixMs;
        IsTombstone = isTombstone;
        DeletedAtUnixMs = deletedAtUnixMs;
        DeletedBy = deletedBy;

        if (reactions is not null)
        {
            _reactions.AddRange(reactions);
        }
    }

    public bool ApplyEdit(string newContent, IReadOnlyList<MentionDto> newMentions, ulong editedAtUnixMs, PublicIdentityId editorId)
    {
        if (IsTombstone)
        {
            return false;
        }

        if (editorId != AuthorId)
        {
            return false;
        }

        if (editedAtUnixMs < TimestampUnixMs || (editedAtUnixMs - TimestampUnixMs) > MaxEditWindowMs)
        {
            return false;
        }

        Content = newContent;
        Mentions = newMentions;
        EditedAtUnixMs = editedAtUnixMs;
        return true;
    }

    public bool ApplyTombstone(PublicIdentityId requestedBy, ulong deletedAtUnixMs)
    {
        if (IsTombstone)
        {
            return false;
        }

        IsTombstone = true;
        Content = "This message was deleted";
        Mentions = Array.Empty<MentionDto>();
        LinkPreview = null;
        Attachments = Array.Empty<MediaAttachmentDto>();
        DeletedAtUnixMs = deletedAtUnixMs;
        DeletedBy = requestedBy;
        return true;
    }

    public void AddOrUpdateReaction(ReactionUpdateDto reaction)
    {
        _reactions.RemoveAll(r => r.AuthorId == reaction.AuthorId && r.Emoji == reaction.Emoji);
        if (reaction.Action == ReactionAction.Add)
        {
            _reactions.Add(reaction);
        }
    }

    public void RemoveReaction(PublicIdentityId authorId, string emoji)
    {
        _reactions.RemoveAll(r => r.AuthorId == authorId && r.Emoji == emoji);
    }

    public bool IsExpired(ulong currentTimestampUnixMs)
    {
        return ExpiresAtUnixMs.HasValue && currentTimestampUnixMs >= ExpiresAtUnixMs.Value;
    }
}
