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

public sealed class SqlChatMessageQueryService : IChatMessageQueryService
{
    private readonly PercolatorDbContext _dbContext;

    public SqlChatMessageQueryService(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<ChatMessageReadModel>> GetMessagesAsync(
        ChannelId channelId,
        ulong? beforeTimestampMs,
        int limit,
        ulong currentTimestampMs,
        CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<ChatMessageReadModel>();
        }

        var channelGuid = channelId.Value;

        var query = _dbContext.ChatMessages
            .AsNoTracking()
            .Where(m => m.ChannelId == channelGuid &&
                        (m.ExpiresAtUnixMs == null || m.ExpiresAtUnixMs > currentTimestampMs));

        if (beforeTimestampMs.HasValue)
        {
            query = query.Where(m => m.TimestampUnixMs < beforeTimestampMs.Value);
        }

        var entities = await query
            .OrderByDescending(m => m.TimestampUnixMs)
            .Take(limit)
            .ToListAsync(ct);

        if (entities.Count == 0)
        {
            return Array.Empty<ChatMessageReadModel>();
        }

        // Chronological order for presentation
        entities.Reverse();

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

        var result = new List<ChatMessageReadModel>(entities.Count);
        foreach (var entity in entities)
        {
            ReplyContextDto? replyContext = entity.ReplyParentId.HasValue && entity.ReplyAuthorId != null && entity.ReplyAuthorId.Length == 16
                ? new ReplyContextDto(
                    PayloadId.FromGuid(entity.ReplyParentId.Value),
                    PublicIdentityId.FromGuid(new Guid(entity.ReplyAuthorId, bigEndian: true)))
                : null;

            mentionsByMessage.TryGetValue(entity.Id, out var msgMentions);
            var mentionDtos = new List<MentionDto>();
            if (msgMentions != null)
            {
                foreach (var m in msgMentions)
                {
                    if (m.IdentityId.Length == 16)
                    {
                        mentionDtos.Add(new MentionDto(
                            PublicIdentityId.FromGuid(new Guid(m.IdentityId, bigEndian: true)),
                            m.StartOffset,
                            m.Length));
                    }
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

            attachmentsByMessage.TryGetValue(entity.Id, out var msgAttachments);
            var attachmentDtos = new List<MediaAttachmentDto>();
            if (msgAttachments != null)
            {
                foreach (var a in msgAttachments)
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

                    attachmentDtos.Add(new MediaAttachmentDto(
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
            }

            reactionsByMessage.TryGetValue(entity.Id, out var msgReactions);
            var reactionGroups = BuildReactionGroups(msgReactions);

            result.Add(new ChatMessageReadModel(
                PayloadId.FromGuid(entity.Id),
                ChannelId.FromGuid(entity.ChannelId),
                PublicIdentityId.FromGuid(new Guid(entity.AuthorId, bigEndian: true)),
                entity.TimestampUnixMs,
                entity.Content,
                mentionDtos,
                replyContext,
                linkPreview,
                attachmentDtos,
                entity.EditedAtUnixMs,
                entity.IsTombstone,
                reactionGroups));
        }

        return result;
    }

    private static IReadOnlyList<ReactionGroupReadModel> BuildReactionGroups(List<ChatReactionEntity>? reactions)
    {
        if (reactions == null || reactions.Count == 0)
        {
            return Array.Empty<ReactionGroupReadModel>();
        }

        return reactions
            .GroupBy(r => r.Emoji)
            .Select(g => new ReactionGroupReadModel(
                g.Key,
                g.Count(),
                g.Where(r => r.ReactorId.Length == 16)
                 .Select(r => PublicIdentityId.FromGuid(new Guid(r.ReactorId, bigEndian: true)))
                 .ToList(),
                HasReactedByCurrentUser: false))
            .ToList();
    }
}
