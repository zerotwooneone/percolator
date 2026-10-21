using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed class ChatMessage
{
    public const int MaxContentLength = 16_000;
    public const int MaxExpireAfterSeconds = 28 * 24 * 60 * 60; // 28 days
    public const ulong MaxEditWindowMs = 24UL * 60 * 60 * 1000; // 24 hours
    public const ulong MaxTombstoneWindowMs = 24UL * 60 * 60 * 1000; // 24 hours

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
    public ulong? ExpiresAtUnixMs { get; private set; }
    public ulong? ReadAtUnixMs { get; private set; }
    public ulong? EditedAtUnixMs { get; private set; }
    public bool IsTombstone { get; private set; }
    public ulong? DeletedAtUnixMs { get; private set; }
    public PublicIdentityId? DeletedBy { get; private set; }

    private readonly List<ChatReaction> _reactions = new();
    public IReadOnlyList<ChatReaction> Reactions => _reactions;

    public ChatMessage(
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
        IEnumerable<ChatReaction>? reactions = null,
        ulong? readAtUnixMs = null)
    {
        bool hasContent = !string.IsNullOrWhiteSpace(content);
        bool hasAttachments = attachments is not null && attachments.Count > 0;
        if (!hasContent && !hasAttachments && !isTombstone)
        {
            throw new ArgumentException("A message must have non-empty content or at least one attachment unless tombstoned.", nameof(content));
        }

        if (content is not null && content.Length > MaxContentLength)
        {
            throw new ArgumentException($"Message content cannot exceed {MaxContentLength} characters.", nameof(content));
        }

        if (expireAfterSeconds.HasValue && (expireAfterSeconds.Value <= 0 || expireAfterSeconds.Value > MaxExpireAfterSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(expireAfterSeconds),
                $"ExpireAfterSeconds must be between 1 and {MaxExpireAfterSeconds} seconds.");
        }

        Id = id;
        ChannelId = channelId;
        AuthorId = authorId;
        TimestampUnixMs = timestampUnixMs;
        Content = content ?? string.Empty;
        Mentions = mentions ?? Array.Empty<MentionDto>();
        ReplyContext = replyContext;
        LinkPreview = linkPreview;
        Attachments = attachments ?? Array.Empty<MediaAttachmentDto>();
        ExpireAfterSeconds = expireAfterSeconds;
        ExpiresAtUnixMs = expiresAtUnixMs;
        ReadAtUnixMs = readAtUnixMs;
        EditedAtUnixMs = editedAtUnixMs;
        IsTombstone = isTombstone;
        DeletedAtUnixMs = deletedAtUnixMs;
        DeletedBy = deletedBy;

        if (reactions is not null)
        {
            _reactions.AddRange(reactions);
        }
    }

    public DomainResult ApplyEdit(
        string newContent,
        IReadOnlyList<MentionDto> newMentions,
        ulong editedAtUnixMs,
        PublicIdentityId editorId,
        ulong? receivedAtUnixMs = null)
    {
        if (IsTombstone)
        {
            return DomainResult.Failure(new DomainError("ALREADY_TOMBSTONED", "Message is already tombstoned."));
        }

        if (editorId != AuthorId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Only the author can edit this message."));
        }

        if (string.IsNullOrWhiteSpace(newContent))
        {
            return DomainResult.Failure(new DomainError("INVALID_CONTENT", "Message content cannot be empty."));
        }

        if (newContent.Length > MaxContentLength)
        {
            return DomainResult.Failure(new DomainError("CONTENT_TOO_LONG", $"Message content cannot exceed {MaxContentLength} characters."));
        }

        if (editedAtUnixMs < TimestampUnixMs || (editedAtUnixMs - TimestampUnixMs) > MaxEditWindowMs)
        {
            return DomainResult.Failure(new DomainError("EDIT_WINDOW_EXPIRED", "Message edit window has expired."));
        }

        if (receivedAtUnixMs.HasValue &&
            (receivedAtUnixMs.Value < TimestampUnixMs || (receivedAtUnixMs.Value - TimestampUnixMs) > MaxEditWindowMs))
        {
            return DomainResult.Failure(new DomainError("EDIT_WINDOW_EXPIRED", "Message edit window has expired at receiver."));
        }

        Content = newContent;
        Mentions = newMentions;
        EditedAtUnixMs = editedAtUnixMs;
        return DomainResult.Success();
    }

    public DomainResult ApplyTombstone(
        PublicIdentityId requestedBy,
        ulong deletedAtUnixMs,
        bool isChannelAdmin = false,
        ulong? receivedAtUnixMs = null)
    {
        if (IsTombstone)
        {
            return DomainResult.Failure(new DomainError("ALREADY_TOMBSTONED", "Message is already tombstoned."));
        }

        if (requestedBy != AuthorId && !isChannelAdmin)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Only the author or a channel administrator can delete this message."));
        }

        if (deletedAtUnixMs < TimestampUnixMs || (deletedAtUnixMs - TimestampUnixMs) > MaxTombstoneWindowMs)
        {
            return DomainResult.Failure(new DomainError("TOMBSTONE_WINDOW_EXPIRED", "Message tombstone window has expired."));
        }

        if (receivedAtUnixMs.HasValue &&
            (receivedAtUnixMs.Value < TimestampUnixMs || (receivedAtUnixMs.Value - TimestampUnixMs) > MaxTombstoneWindowMs))
        {
            return DomainResult.Failure(new DomainError("TOMBSTONE_WINDOW_EXPIRED", "Message tombstone window has expired at receiver."));
        }

        IsTombstone = true;
        Content = requestedBy != AuthorId && isChannelAdmin
            ? "This message was deleted by a channel administrator"
            : "This message was deleted";
        Mentions = Array.Empty<MentionDto>();
        LinkPreview = null;
        Attachments = Array.Empty<MediaAttachmentDto>();
        DeletedAtUnixMs = deletedAtUnixMs;
        DeletedBy = requestedBy;
        return DomainResult.Success();
    }

    /// <summary>
    /// Marks the message as read at the given timestamp and starts the disappearing-message countdown
    /// for 1:1 messages that defer expiration until read.
    /// </summary>
    public void MarkAsRead(ulong readAtUnixMs)
    {
        ReadAtUnixMs ??= readAtUnixMs;

        if (ExpireAfterSeconds is > 0)
        {
            ulong readBasedExpiry = ReadAtUnixMs.Value + ((ulong)ExpireAfterSeconds.Value * 1000UL);
            ExpiresAtUnixMs = ExpiresAtUnixMs.HasValue
                ? Math.Min(ExpiresAtUnixMs.Value, readBasedExpiry)
                : readBasedExpiry;
        }
    }

    public void AddReaction(PublicIdentityId reactorId, Emoji emoji, ulong timestampUnixMs)
    {
        _reactions.RemoveAll(r => r.ReactorId == reactorId && r.Emoji == emoji);
        _reactions.Add(new ChatReaction(reactorId, emoji, timestampUnixMs));
    }

    public void RemoveReaction(PublicIdentityId reactorId, Emoji emoji)
    {
        _reactions.RemoveAll(r => r.ReactorId == reactorId && r.Emoji == emoji);
    }

    public bool IsExpired(ulong currentTimestampUnixMs)
    {
        return ExpiresAtUnixMs.HasValue && currentTimestampUnixMs >= ExpiresAtUnixMs.Value;
    }
}
