namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

public sealed class SqlConversationListQueryService : IConversationListQueryService
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public SqlConversationListQueryService(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    }

    private async Task<TResult> WithContextAsync<TResult>(Func<PercolatorDbContext, Task<TResult>> query)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await query(db).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ConversationSummaryReadModel>> GetConversationsAsync(
        Domain.Identities.PublicIdentityId currentUserId,
        ulong currentTimestampMs,
        CancellationToken ct = default)
    {
        return WithContextAsync(async db =>
        {
            var currentUserGuid = currentUserId.Value;
            byte[] currentUserBytes = currentUserId.ToByteArray();

            // 1. Identify channels accessible to current user
            var directChannelIds = await db.ChatChannels
                .AsNoTracking()
                .Where(c => c.OwnerIdentityId == currentUserGuid || c.RemotePeerId == currentUserGuid)
                .Select(c => c.Id)
                .ToListAsync(ct).ConfigureAwait(false);

            var groupChannelIds = await db.ChatChannelMembers
                .AsNoTracking()
                .Where(m => m.MemberIdentityId == currentUserGuid)
                .Select(m => m.ChannelId)
                .ToListAsync(ct).ConfigureAwait(false);

            var userChannelIds = directChannelIds.Union(groupChannelIds).Distinct().ToList();

            // If no channels exist in ChatChannels yet, check channels with messages involving current user
            if (userChannelIds.Count == 0)
            {
                userChannelIds = await db.ChatMessages
                    .AsNoTracking()
                    .Where(m => m.AuthorId == currentUserBytes)
                    .Select(m => m.ChannelId)
                    .Distinct()
                    .ToListAsync(ct).ConfigureAwait(false);
            }

            if (userChannelIds.Count == 0)
            {
                return (IReadOnlyList<ConversationSummaryReadModel>)Array.Empty<ConversationSummaryReadModel>();
            }

            // 2. Fetch channel metadata
            var channels = await db.ChatChannels
                .AsNoTracking()
                .Where(c => userChannelIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);

            // 3. For each channel, find the latest message and unread count
            var summaries = new List<ConversationSummaryReadModel>(userChannelIds.Count);

            foreach (var channelId in userChannelIds)
            {
                var latest = await db.ChatMessages
                    .AsNoTracking()
                    .Where(m => m.ChannelId == channelId &&
                                (m.ExpiresAtUnixMs == null || (long)m.ExpiresAtUnixMs > (long)currentTimestampMs))
                    .OrderByDescending(m => m.TimestampUnixMs)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);

                if (latest == null)
                {
                    continue;
                }

                channels.TryGetValue(channelId, out var channel);
                string title = !string.IsNullOrWhiteSpace(channel?.Name)
                    ? channel.Name
                    : channelId.ToString();

                string snippet;
                if (latest.IsTombstone)
                {
                    snippet = "This message was deleted";
                }
                else if (!string.IsNullOrEmpty(latest.Content))
                {
                    snippet = latest.Content;
                }
                else
                {
                    bool hasAttachment = await db.ChatAttachments
                        .AsNoTracking()
                        .AnyAsync(a => a.MessageId == latest.Id, ct).ConfigureAwait(false);
                    snippet = hasAttachment ? "[Attachment]" : string.Empty;
                }

                // Unread count: unexpired messages not read and not sent by current user
                int unreadCount = await db.ChatMessages
                    .AsNoTracking()
                    .Where(m => m.ChannelId == channelId &&
                                (m.ExpiresAtUnixMs == null || (long)m.ExpiresAtUnixMs > (long)currentTimestampMs) &&
                                m.ReadAtUnixMs == null &&
                                m.AuthorId != currentUserBytes)
                    .CountAsync(ct).ConfigureAwait(false);

                Domain.Identities.PublicIdentityId? lastAuthorId = latest.AuthorId.Length == 16
                    ? Domain.Identities.PublicIdentityId.FromBytes(latest.AuthorId)
                    : null;

                summaries.Add(new ConversationSummaryReadModel(
                    ChannelId.FromGuid(channelId),
                    title,
                    snippet,
                    latest.TimestampUnixMs,
                    unreadCount,
                    lastAuthorId));
            }

            return (IReadOnlyList<ConversationSummaryReadModel>)summaries.OrderByDescending(s => s.LastMessageTimestampUnixMs).ToList();
        });
    }
}
