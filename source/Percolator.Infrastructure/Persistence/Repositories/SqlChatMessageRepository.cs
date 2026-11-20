namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.PluginSdk;

public sealed class SqlChatMessageRepository : IChatMessageRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqlChatMessageRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(ChatMessage record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var entity = new ChatMessageEntity
        {
            Id = record.Id.Value,
            ChannelId = record.ChannelId.Value,
            AuthorId = record.AuthorId.ToByteArray(),
            TimestampUnixMs = record.TimestampUnixMs,
            Content = record.Content ?? string.Empty,
            ExpireAfterSeconds = record.ExpireAfterSeconds,
            ExpiresAtUnixMs = record.ExpiresAtUnixMs,
            DeliveredAtUnixMs = record.DeliveredAtUnixMs,
            ReadAtUnixMs = record.ReadAtUnixMs,
            EditedAtUnixMs = record.EditedAtUnixMs,
            IsTombstone = record.IsTombstone,
            DeletedAtUnixMs = record.DeletedAtUnixMs,
            DeletedBy = record.DeletedBy.HasValue ? record.DeletedBy.Value.ToByteArray() : null,
            ReplyParentId = record.ReplyContext?.ParentPayloadId.Value,
            ReplyAuthorId = record.ReplyContext != null
                ? record.ReplyContext.ParentAuthorId.ToByteArray()
                : null
        };

        if (record.LinkPreview != null)
        {
            entity.LinkPreviewTitle = record.LinkPreview.Title;
            entity.LinkPreviewDescription = record.LinkPreview.Description;
            entity.LinkPreviewCanonicalUrl = record.LinkPreview.CanonicalUrl;

            if (record.LinkPreview.ThumbnailBlob != null)
            {
                var tb = record.LinkPreview.ThumbnailBlob;
                entity.LinkPreviewThumbnailBlobId = tb.Id.HexDigest;
                entity.LinkPreviewThumbnailSha256 = tb.CiphertextSha256;
                entity.LinkPreviewThumbnailKey = tb.EncryptionKey;
                entity.LinkPreviewThumbnailNonce = tb.BaseNonce;
                entity.LinkPreviewThumbnailStorageMode = (int)tb.StorageMode;
                entity.LinkPreviewThumbnailRelayUri = tb.RelayEndpointUri;
                entity.LinkPreviewThumbnailCiphertextSize = tb.CiphertextSizeBytes;
                entity.LinkPreviewThumbnailMimeType = tb.MimeType;
            }
        }

        _dbContext.ChatMessages.Add(entity);

        if (record.Mentions is { Count: > 0 })
        {
            foreach (var mention in record.Mentions)
            {
                _dbContext.ChatMessageMentions.Add(new ChatMessageMentionEntity
                {
                    Id = Guid.NewGuid(),
                    MessageId = record.Id.Value,
                    IdentityId = mention.IdentityId.ToByteArray(),
                    StartOffset = mention.StartOffset,
                    Length = mention.Length
                });
            }
        }

        if (record.Reactions is { Count: > 0 })
        {
            foreach (var reaction in record.Reactions)
            {
                _dbContext.ChatReactions.Add(new ChatReactionEntity
                {
                    MessageId = record.Id.Value,
                    ReactorId = reaction.ReactorId.ToByteArray(),
                    Emoji = reaction.Emoji.Value,
                    TimestampUnixMs = reaction.ReactedAtUnixMs
                });
            }
        }

        if (record.Attachments is { Count: > 0 })
        {
            foreach (var attachment in record.Attachments)
            {
                _dbContext.ChatAttachments.Add(new ChatAttachmentEntity
                {
                    Id = Guid.NewGuid(),
                    MessageId = record.Id.Value,
                    BlobId = attachment.BlobId.HexDigest,
                    BlobSha256 = attachment.OutOfBandReference?.CiphertextSha256 ?? Array.Empty<byte>(),
                    FileName = attachment.FileName,
                    FileSizeBytes = attachment.ByteSize,
                    MimeType = attachment.ContentType ?? string.Empty,
                    EncryptionKey = attachment.OutOfBandReference?.EncryptionKey ?? Array.Empty<byte>(),
                    BaseNonce = attachment.OutOfBandReference?.BaseNonce ?? Array.Empty<byte>(),
                    StorageMode = (int)(attachment.OutOfBandReference?.StorageMode ?? BlobStorageMode.DirectP2P),
                    RelayEndpointUri = attachment.OutOfBandReference?.RelayEndpointUri,
                    CiphertextSizeBytes = attachment.OutOfBandReference?.CiphertextSizeBytes ?? 0,
                    Width = attachment.Width,
                    Height = attachment.Height,
                    Waveform = attachment.Waveform,
                    InlineBytes = attachment.InlineBytes?.ToArray()
                });
            }
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<ChatMessage?> GetByIdAsync(PayloadId id, CancellationToken ct = default)
    {
        var guid = id.Value;
        var entity = await _dbContext.ChatMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == guid, ct);

        if (entity == null)
        {
            return null;
        }

        var reactions = await _dbContext.ChatReactions
            .AsNoTracking()
            .Where(r => r.MessageId == guid)
            .ToListAsync(ct);

        var mentions = await _dbContext.ChatMessageMentions
            .AsNoTracking()
            .Where(m => m.MessageId == guid)
            .ToListAsync(ct);

        var attachments = await _dbContext.ChatAttachments
            .AsNoTracking()
            .Where(a => a.MessageId == guid)
            .ToListAsync(ct);

        return ToDomainModel(entity, reactions, mentions, attachments);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetByChannelIdAsync(ChannelId channelId, CancellationToken ct = default)
    {
        var channelGuid = channelId.Value;
        var entities = await _dbContext.ChatMessages
            .AsNoTracking()
            .Where(m => m.ChannelId == channelGuid)
            .OrderBy(m => m.TimestampUnixMs)
            .ToListAsync(ct);

        if (entities.Count == 0)
        {
            return Array.Empty<ChatMessage>();
        }

        var messageIds = entities.Select(e => e.Id).ToList();

        var reactions = await _dbContext.ChatReactions
            .AsNoTracking()
            .Where(r => messageIds.Contains(r.MessageId))
            .ToListAsync(ct);

        var mentions = await _dbContext.ChatMessageMentions
            .AsNoTracking()
            .Where(m => messageIds.Contains(m.MessageId))
            .ToListAsync(ct);

        var attachments = await _dbContext.ChatAttachments
            .AsNoTracking()
            .Where(a => messageIds.Contains(a.MessageId))
            .ToListAsync(ct);

        var reactionsByMessage = reactions.GroupBy(r => r.MessageId).ToDictionary(g => g.Key, g => g.ToList());
        var mentionsByMessage = mentions.GroupBy(m => m.MessageId).ToDictionary(g => g.Key, g => g.ToList());
        var attachmentsByMessage = attachments.GroupBy(a => a.MessageId).ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<ChatMessage>(entities.Count);
        foreach (var entity in entities)
        {
            reactionsByMessage.TryGetValue(entity.Id, out var msgReactions);
            mentionsByMessage.TryGetValue(entity.Id, out var msgMentions);
            attachmentsByMessage.TryGetValue(entity.Id, out var msgAttachments);

            result.Add(ToDomainModel(
                entity,
                msgReactions ?? new List<ChatReactionEntity>(),
                msgMentions ?? new List<ChatMessageMentionEntity>(),
                msgAttachments ?? new List<ChatAttachmentEntity>()));
        }

        return result;
    }

    public async Task UpdateAsync(ChatMessage record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var guid = record.Id.Value;
        var entity = await _dbContext.ChatMessages
            .FirstOrDefaultAsync(m => m.Id == guid, ct);

        if (entity == null)
        {
            throw new InvalidOperationException($"ChatMessage with ID {record.Id} was not found.");
        }

        entity.Content = record.Content ?? string.Empty;
        entity.ExpiresAtUnixMs = record.ExpiresAtUnixMs;
        entity.DeliveredAtUnixMs = record.DeliveredAtUnixMs;
        entity.ReadAtUnixMs = record.ReadAtUnixMs;
        entity.EditedAtUnixMs = record.EditedAtUnixMs;
        entity.IsTombstone = record.IsTombstone;
        entity.DeletedAtUnixMs = record.DeletedAtUnixMs;
        entity.DeletedBy = record.DeletedBy.HasValue ? record.DeletedBy.Value.ToByteArray() : null;

        // If tombstoned, clear link preview and attachments
        if (record.IsTombstone)
        {
            entity.LinkPreviewTitle = null;
            entity.LinkPreviewDescription = null;
            entity.LinkPreviewCanonicalUrl = null;
            entity.LinkPreviewThumbnailBlobId = null;
            entity.LinkPreviewThumbnailSha256 = null;
            entity.LinkPreviewThumbnailKey = null;
            entity.LinkPreviewThumbnailNonce = null;
            entity.LinkPreviewThumbnailStorageMode = null;
            entity.LinkPreviewThumbnailRelayUri = null;
            entity.LinkPreviewThumbnailCiphertextSize = null;
            entity.LinkPreviewThumbnailMimeType = null;

            var existingAttachments = await _dbContext.ChatAttachments
                .Where(a => a.MessageId == guid)
                .ToListAsync(ct);
            _dbContext.ChatAttachments.RemoveRange(existingAttachments);

            var existingMentions = await _dbContext.ChatMessageMentions
                .Where(m => m.MessageId == guid)
                .ToListAsync(ct);
            _dbContext.ChatMessageMentions.RemoveRange(existingMentions);
        }
        else
        {
            // Replace mentions
            var existingMentions = await _dbContext.ChatMessageMentions
                .Where(m => m.MessageId == guid)
                .ToListAsync(ct);
            _dbContext.ChatMessageMentions.RemoveRange(existingMentions);

            if (record.Mentions is { Count: > 0 })
            {
                foreach (var mention in record.Mentions)
                {
                    _dbContext.ChatMessageMentions.Add(new ChatMessageMentionEntity
                    {
                        Id = Guid.NewGuid(),
                        MessageId = guid,
                        IdentityId = mention.IdentityId.ToByteArray(),
                        StartOffset = mention.StartOffset,
                        Length = mention.Length
                    });
                }
            }
        }

        // Replace reactions
        var existingReactions = await _dbContext.ChatReactions
            .Where(r => r.MessageId == guid)
            .ToListAsync(ct);
        _dbContext.ChatReactions.RemoveRange(existingReactions);

        if (record.Reactions is { Count: > 0 })
        {
            foreach (var reaction in record.Reactions)
            {
                _dbContext.ChatReactions.Add(new ChatReactionEntity
                {
                    MessageId = guid,
                    ReactorId = reaction.ReactorId.ToByteArray(),
                    Emoji = reaction.Emoji.Value,
                    TimestampUnixMs = reaction.ReactedAtUnixMs
                });
            }
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<int> PurgeExpiredMessagesAsync(ulong currentTimestampUnixMs, CancellationToken ct = default)
    {
        var expired = await _dbContext.ChatMessages
            .Where(m => m.ExpiresAtUnixMs.HasValue && m.ExpiresAtUnixMs.Value <= currentTimestampUnixMs)
            .ToListAsync(ct);

        if (expired.Count == 0)
        {
            return 0;
        }

        _dbContext.ChatMessages.RemoveRange(expired);
        await _dbContext.SaveChangesAsync(ct);
        return expired.Count;
    }

    private static ChatMessage ToDomainModel(
        ChatMessageEntity entity,
        List<ChatReactionEntity> reactionEntities,
        List<ChatMessageMentionEntity> mentionEntities,
        List<ChatAttachmentEntity> attachmentEntities)
    {
        ReplyContextDto? replyContext = entity.ReplyParentId.HasValue && entity.ReplyAuthorId != null && entity.ReplyAuthorId.Length == 16
            ? new ReplyContextDto(
                PayloadId.FromGuid(entity.ReplyParentId.Value),
                Domain.Identities.PublicIdentityId.FromBytes(entity.ReplyAuthorId))
            : null;

        var mentions = new List<MentionDto>(mentionEntities.Count);
        foreach (var m in mentionEntities)
        {
            if (m.IdentityId.Length == 16)
            {
                mentions.Add(new MentionDto(
                    Domain.Identities.PublicIdentityId.FromBytes(m.IdentityId),
                    m.StartOffset,
                    m.Length));
            }
        }

        LinkPreviewDto? linkPreview = null;
        if (!string.IsNullOrEmpty(entity.LinkPreviewTitle) || !string.IsNullOrEmpty(entity.LinkPreviewCanonicalUrl))
        {
            BlobReference? thumbnailBlob = null;
            if (!string.IsNullOrEmpty(entity.LinkPreviewThumbnailBlobId) &&
                entity.LinkPreviewThumbnailSha256 != null &&
                entity.LinkPreviewThumbnailKey != null &&
                entity.LinkPreviewThumbnailNonce != null)
            {
                thumbnailBlob = new BlobReference
                {
                    Id = new BlobId(entity.LinkPreviewThumbnailBlobId),
                    CiphertextSha256 = entity.LinkPreviewThumbnailSha256,
                    EncryptionKey = entity.LinkPreviewThumbnailKey,
                    BaseNonce = entity.LinkPreviewThumbnailNonce,
                    PlaintextSizeBytes = 0,
                    CiphertextSizeBytes = entity.LinkPreviewThumbnailCiphertextSize ?? 0,
                    MimeType = entity.LinkPreviewThumbnailMimeType ?? "image/jpeg",
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
                    StorageMode = (BlobStorageMode)(entity.LinkPreviewThumbnailStorageMode ?? (int)BlobStorageMode.DirectP2P),
                    RelayEndpointUri = entity.LinkPreviewThumbnailRelayUri
                };
            }

            linkPreview = new LinkPreviewDto(
                entity.LinkPreviewTitle ?? string.Empty,
                entity.LinkPreviewDescription ?? string.Empty,
                entity.LinkPreviewCanonicalUrl ?? string.Empty,
                thumbnailBlob);
        }

        var attachments = new List<MediaAttachmentDto>(attachmentEntities.Count);
        foreach (var a in attachmentEntities)
        {
            BlobReference? outOfBand = null;
            if (a.EncryptionKey is { Length: > 0 } && a.BlobSha256 is { Length: > 0 })
            {
                outOfBand = new BlobReference
                {
                    Id = new BlobId(a.BlobId),
                    CiphertextSha256 = a.BlobSha256,
                    EncryptionKey = a.EncryptionKey,
                    BaseNonce = a.BaseNonce,
                    PlaintextSizeBytes = a.FileSizeBytes,
                    CiphertextSizeBytes = a.CiphertextSizeBytes,
                    MimeType = a.MimeType,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
                    StorageMode = (BlobStorageMode)a.StorageMode,
                    RelayEndpointUri = a.RelayEndpointUri,
                    Waveform = a.Waveform,
                    Width = a.Width,
                    Height = a.Height
                };
            }

            attachments.Add(new MediaAttachmentDto(
                new BlobId(a.BlobId),
                a.FileName,
                a.MimeType,
                a.FileSizeBytes,
                a.Width,
                a.Height,
                a.Waveform,
                a.InlineBytes,
                outOfBand));
        }

        Domain.Identities.PublicIdentityId? deletedBy = entity.DeletedBy != null && entity.DeletedBy.Length == 16
            ? Domain.Identities.PublicIdentityId.FromBytes(entity.DeletedBy)
            : null;

        var chatReactions = new List<ChatReaction>(reactionEntities.Count);
        foreach (var r in reactionEntities)
        {
            var emojiResult = Emoji.Create(r.Emoji);
            if (emojiResult.IsSuccess && r.ReactorId.Length == 16)
            {
                chatReactions.Add(new ChatReaction(
                    Domain.Identities.PublicIdentityId.FromBytes(r.ReactorId),
                    emojiResult.Value,
                    r.TimestampUnixMs));
            }
        }

        return new ChatMessage(
            PayloadId.FromGuid(entity.Id),
            ChannelId.FromGuid(entity.ChannelId),
            Domain.Identities.PublicIdentityId.FromBytes(entity.AuthorId),
            entity.TimestampUnixMs,
            entity.Content,
            mentions,
            replyContext,
            linkPreview,
            attachments,
            entity.ExpireAfterSeconds,
            entity.ExpiresAtUnixMs,
            entity.EditedAtUnixMs,
            entity.IsTombstone,
            entity.DeletedAtUnixMs,
            deletedBy,
            chatReactions,
            readAtUnixMs: entity.ReadAtUnixMs,
            deliveredAtUnixMs: entity.DeliveredAtUnixMs);
    }
}
