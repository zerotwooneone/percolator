namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
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

            // 1. Identify direct and group channels accessible to current user
            var directChannels = await db.ChatChannels
                .AsNoTracking()
                .Where(c => c.Type == 0 && (c.OwnerIdentityId == currentUserGuid || c.RemotePeerId == currentUserGuid))
                .ToListAsync(ct).ConfigureAwait(false);

            var groupChannelIds = await db.ChatChannelMembers
                .AsNoTracking()
                .Where(m => m.MemberIdentityId == currentUserGuid)
                .Select(m => m.ChannelId)
                .ToListAsync(ct).ConfigureAwait(false);

            var groupChannels = await db.ChatChannels
                .AsNoTracking()
                .Where(c => c.Type == 1 && groupChannelIds.Contains(c.Id))
                .ToListAsync(ct).ConfigureAwait(false);

            var channels = directChannels.Concat(groupChannels).ToDictionary(c => c.Id);

            if (channels.Count == 0)
            {
                return (IReadOnlyList<ConversationSummaryReadModel>)Array.Empty<ConversationSummaryReadModel>();
            }

            // 2. Batch lookup for contacts, profiles, routing, and relays
            var peerGuids = channels.Values
                .Where(c => c.Type == 0)
                .Select(c => c.OwnerIdentityId == currentUserGuid ? c.RemotePeerId : c.OwnerIdentityId)
                .Where(g => g.HasValue)
                .Select(g => g!.Value)
                .Distinct()
                .ToList();

            var contacts = await db.PeerContacts
                .AsNoTracking()
                .Where(c => c.OwnerIdentityId == currentUserGuid && peerGuids.Contains(c.RemotePeerId))
                .ToDictionaryAsync(c => c.RemotePeerId, ct).ConfigureAwait(false);

            var profiles = await db.PeerProfiles
                .AsNoTracking()
                .Where(p => peerGuids.Contains(p.PeerId))
                .ToDictionaryAsync(p => p.PeerId, ct).ConfigureAwait(false);

            var routings = await db.PeerMailboxRouting
                .AsNoTracking()
                .Where(r => peerGuids.Contains(r.PeerId))
                .ToDictionaryAsync(r => r.PeerId, ct).ConfigureAwait(false);

            var relays = await db.RelayRegistrations
                .AsNoTracking()
                .ToListAsync(ct).ConfigureAwait(false);

            var channelIdList = channels.Keys.ToList();
            var unexpiredMessages = await db.ChatMessages
                .AsNoTracking()
                .Where(m => channelIdList.Contains(m.ChannelId) &&
                            (m.ExpiresAtUnixMs == null || (long)m.ExpiresAtUnixMs > (long)currentTimestampMs))
                .ToListAsync(ct).ConfigureAwait(false);

            var latestByChannel = unexpiredMessages
                .GroupBy(m => m.ChannelId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.TimestampUnixMs).First());

            var unreadCountByChannel = unexpiredMessages
                .Where(m => m.ReadAtUnixMs == null && !m.AuthorId.AsSpan().SequenceEqual(currentUserBytes))
                .GroupBy(m => m.ChannelId)
                .ToDictionary(g => g.Key, g => g.Count());

            var candidateAttachmentMessageIds = latestByChannel.Values
                .Where(m => string.IsNullOrEmpty(m.Content) && !m.IsTombstone)
                .Select(m => m.Id)
                .ToList();

            var attachmentMessageIds = candidateAttachmentMessageIds.Count > 0
                ? (await db.ChatAttachments
                    .AsNoTracking()
                    .Where(a => candidateAttachmentMessageIds.Contains(a.MessageId))
                    .Select(a => a.MessageId)
                    .Distinct()
                    .ToListAsync(ct).ConfigureAwait(false)).ToHashSet()
                : new HashSet<Guid>();

            // 3. Project each channel into ConversationSummaryReadModel (retaining 0-message channels)
            var summaries = new List<ConversationSummaryReadModel>(channels.Count);

            foreach (var channel in channels.Values)
            {
                var kind = channel.Type == 1 ? ConversationChannelKind.Group : ConversationChannelKind.Direct;
                Domain.Identities.PublicIdentityId? remotePeerId = null;
                string title;
                bool isPendingAcceptance = false;
                string? relayEndpoint = channel.RelayEndpoint;

                if (kind == ConversationChannelKind.Direct)
                {
                    var remoteGuid = channel.OwnerIdentityId == currentUserGuid ? channel.RemotePeerId : channel.OwnerIdentityId;
                    if (remoteGuid.HasValue)
                    {
                        remotePeerId = new Domain.Identities.PublicIdentityId(remoteGuid.Value);
                    }

                    if (remoteGuid.HasValue && contacts.TryGetValue(remoteGuid.Value, out var contact) &&
                        !string.IsNullOrWhiteSpace(contact.Nickname) &&
                        !contact.Nickname.Equals(remoteGuid.Value.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        title = contact.Nickname;
                    }
                    else if (remoteGuid.HasValue && profiles.TryGetValue(remoteGuid.Value, out var profile) &&
                             !string.IsNullOrWhiteSpace(profile.DisplayName))
                    {
                        title = profile.DisplayName;
                    }
                    else if (!string.IsNullOrWhiteSpace(channel.Name))
                    {
                        title = channel.Name;
                    }
                    else if (remoteGuid.HasValue)
                    {
                        title = $"Peer {remoteGuid.Value:N}"[..12];
                    }
                    else
                    {
                        title = channel.Id.ToString();
                    }

                    isPendingAcceptance = (DirectChannelStatus)channel.Status == DirectChannelStatus.PendingAcceptance;

                    if (string.IsNullOrWhiteSpace(relayEndpoint) && remoteGuid.HasValue &&
                        routings.TryGetValue(remoteGuid.Value, out var routing) &&
                        !string.IsNullOrWhiteSpace(routing.RelayEndpoint))
                    {
                        relayEndpoint = routing.RelayEndpoint;
                    }
                }
                else
                {
                    title = !string.IsNullOrWhiteSpace(channel.Name) ? channel.Name : "Group";
                    isPendingAcceptance = false;
                }

                Domain.Identities.PublicIdentityId? relayIdentityId = null;
                if (!string.IsNullOrWhiteSpace(relayEndpoint))
                {
                    var targetEpResult = RelayEndpoint.Create(relayEndpoint);
                    if (targetEpResult.IsSuccess)
                    {
                        var targetEp = targetEpResult.Value!;
                        var matchedRelay = relays.FirstOrDefault(r =>
                        {
                            var regEpResult = RelayEndpoint.Create(r.Endpoint);
                            return regEpResult.IsSuccess && regEpResult.Value == targetEp;
                        });
                        if (matchedRelay != null)
                        {
                            relayIdentityId = new Domain.Identities.PublicIdentityId(matchedRelay.RelayIdentityId);
                        }
                    }
                }

                latestByChannel.TryGetValue(channel.Id, out var latest);

                string snippet = string.Empty;
                Domain.Identities.PublicIdentityId? lastAuthorId = null;

                if (latest != null)
                {
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
                        bool hasAttachment = attachmentMessageIds.Contains(latest.Id);
                        snippet = hasAttachment ? "[Attachment]" : string.Empty;
                    }

                    lastAuthorId = latest.AuthorId.Length == 16
                        ? Domain.Identities.PublicIdentityId.FromBytes(latest.AuthorId)
                        : null;
                }

                int unreadCount = unreadCountByChannel.TryGetValue(channel.Id, out int cnt) ? cnt : 0;
                ulong channelActivityMs = (ulong)Math.Max(0L, channel.LastActivityUtc.ToUnixTimeMilliseconds());
                ulong lastActivityTimestamp = latest?.TimestampUnixMs ?? channelActivityMs;

                summaries.Add(new ConversationSummaryReadModel(
                    ChannelId.FromGuid(channel.Id),
                    kind,
                    remotePeerId,
                    title,
                    snippet,
                    lastActivityTimestamp,
                    unreadCount,
                    lastAuthorId,
                    isPendingAcceptance,
                    relayEndpoint,
                    relayIdentityId));
            }

            return (IReadOnlyList<ConversationSummaryReadModel>)summaries
                .OrderByDescending(s => s.LastActivityTimestampUnixMs)
                .ToList();
        });
    }
}
