using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ports;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Adapters.Queries;

public sealed class InMemoryChatMessageQueryService : IChatMessageQueryService
{
    private readonly InMemoryChatMessageRepository _chatRepo;

    public InMemoryChatMessageQueryService(InMemoryChatMessageRepository chatRepo)
    {
        _chatRepo = chatRepo ?? throw new ArgumentNullException(nameof(chatRepo));
    }

    public async Task<IReadOnlyList<ChatMessageReadModel>> GetMessagesAsync(
        ChannelId channelId,
        ulong? beforeTimestampMs,
        int limit,
        ulong currentTimestampMs,
        CancellationToken ct = default)
    {
        var messages = await _chatRepo.GetByChannelIdAsync(channelId, ct).ConfigureAwait(false);

        var query = messages.Where(m => !m.IsExpired(currentTimestampMs));
        if (beforeTimestampMs.HasValue)
        {
            query = query.Where(m => m.TimestampUnixMs < beforeTimestampMs.Value);
        }

        var list = query
            .OrderBy(m => m.TimestampUnixMs)
            .ThenBy(m => m.Id)
            .Take(limit)
            .Select(m => new ChatMessageReadModel(
                m.Id,
                m.ChannelId,
                m.AuthorId,
                m.TimestampUnixMs,
                m.Content,
                m.Mentions,
                m.ReplyContext,
                m.LinkPreview,
                m.Attachments,
                m.EditedAtUnixMs,
                m.IsTombstone,
                Array.Empty<ReactionGroupReadModel>()))
            .ToList();

        return list;
    }
}

public sealed class InMemoryConversationListQueryService : IConversationListQueryService
{
    private readonly InMemoryChannelRepository _channelRepo;
    private readonly InMemoryChatMessageRepository _chatRepo;
    private readonly InMemoryPeerContactRepository _contactRepo;

    public InMemoryConversationListQueryService(
        InMemoryChannelRepository channelRepo,
        InMemoryChatMessageRepository chatRepo,
        InMemoryPeerContactRepository contactRepo)
    {
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _chatRepo = chatRepo ?? throw new ArgumentNullException(nameof(chatRepo));
        _contactRepo = contactRepo ?? throw new ArgumentNullException(nameof(contactRepo));
    }

    public async Task<IReadOnlyList<ConversationSummaryReadModel>> GetConversationsAsync(
        PublicIdentityId currentUserId,
        ulong currentTimestampMs,
        CancellationToken ct = default)
    {
        var summaries = new List<ConversationSummaryReadModel>();

        foreach (var channel in _channelRepo.AllDirect)
        {
            if (channel.OwnerIdentityId != currentUserId && channel.RemotePeerId != currentUserId)
            {
                continue;
            }

            var peerId = channel.OwnerIdentityId == currentUserId ? channel.RemotePeerId : channel.OwnerIdentityId;
            var contact = await _contactRepo.GetByPeerIdAsync(currentUserId, peerId, ct).ConfigureAwait(false);
            var title = contact?.Nickname.Value ?? peerId.ToString();

            var messages = (await _chatRepo.GetByChannelIdAsync(channel.Id, ct).ConfigureAwait(false))
                .Where(m => !m.IsExpired(currentTimestampMs))
                .ToList();
            var lastMsg = messages.LastOrDefault();

            int unreadCount = messages.Count(m => m.AuthorId != currentUserId);

            summaries.Add(new ConversationSummaryReadModel(
                channel.Id,
                title,
                lastMsg?.Content ?? string.Empty,
                lastMsg?.TimestampUnixMs ?? (ulong)channel.CreatedAtUtc.ToUnixTimeMilliseconds(),
                unreadCount,
                lastMsg?.AuthorId));
        }

        foreach (var group in _channelRepo.AllGroup)
        {
            if (!group.Members.Any(m => m.Id == currentUserId))
            {
                continue;
            }

            var messages = (await _chatRepo.GetByChannelIdAsync(group.Id, ct).ConfigureAwait(false))
                .Where(m => !m.IsExpired(currentTimestampMs))
                .ToList();
            var lastMsg = messages.LastOrDefault();

            int unreadCount = messages.Count(m => m.AuthorId != currentUserId);

            summaries.Add(new ConversationSummaryReadModel(
                group.Id,
                group.Name,
                lastMsg?.Content ?? string.Empty,
                lastMsg?.TimestampUnixMs ?? (ulong)group.CreatedAtUtc.ToUnixTimeMilliseconds(),
                unreadCount,
                lastMsg?.AuthorId));
        }

        return summaries.OrderByDescending(s => s.LastMessageTimestampUnixMs).ToList();
    }
}

public sealed class InMemoryOutboxQueryService : IOutboxQueryService
{
    private readonly InMemoryOutboxRepository _outboxRepo;

    public InMemoryOutboxQueryService(InMemoryOutboxRepository outboxRepo)
    {
        _outboxRepo = outboxRepo ?? throw new ArgumentNullException(nameof(outboxRepo));
    }

    public Task<IReadOnlyList<OutboxJobSummaryReadModel>> GetPendingJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        var pending = _outboxRepo.AllJobs
            .Where(j => j.Status == OutboxStatus.Pending)
            .Take(limit)
            .Select(ToReadModel)
            .ToList();
        return Task.FromResult<IReadOnlyList<OutboxJobSummaryReadModel>>(pending);
    }

    public Task<IReadOnlyList<OutboxJobSummaryReadModel>> GetFailedJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        var failed = _outboxRepo.AllJobs
            .Where(j => j.Status == OutboxStatus.Failed)
            .Take(limit)
            .Select(ToReadModel)
            .ToList();
        return Task.FromResult<IReadOnlyList<OutboxJobSummaryReadModel>>(failed);
    }

    public Task<OutboxJobSummaryReadModel?> GetJobSummaryByIdAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = _outboxRepo.AllJobs.FirstOrDefault(j => j.Id == jobId);
        return Task.FromResult(job != null ? ToReadModel(job) : null);
    }

    public Task<IReadOnlyList<OutboxJobSummaryReadModel>> GetJobsForOwnerAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        var jobs = _outboxRepo.AllJobs
            .Where(j => j.OwnerIdentityId == ownerId)
            .Select(ToReadModel)
            .ToList();
        return Task.FromResult<IReadOnlyList<OutboxJobSummaryReadModel>>(jobs);
    }

    private static OutboxJobSummaryReadModel ToReadModel(OutboxJob job)
    {
        return new OutboxJobSummaryReadModel(
            job.Id,
            job.ChannelId,
            job.OwnerIdentityId,
            job.RecipientIdentityId,
            job.Route.Type,
            job.Status,
            job.RetryCount,
            job.MaxAttempts,
            job.CreatedAtUtc,
            job.NextAttemptAtUtc);
    }
}

public sealed class InMemoryPeerContactQueryService : IPeerContactQueryService
{
    private readonly InMemoryPeerContactRepository _contactRepo;

    public InMemoryPeerContactQueryService(InMemoryPeerContactRepository contactRepo)
    {
        _contactRepo = contactRepo ?? throw new ArgumentNullException(nameof(contactRepo));
    }

    public async Task<IReadOnlyList<PeerContactSummaryReadModel>> GetContactSummariesAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        var contacts = await _contactRepo.GetAllForOwnerAsync(ownerId, ct).ConfigureAwait(false);
        var summaries = contacts.Select(c => new PeerContactSummaryReadModel(
            c.RemotePeerId,
            c.Nickname.Value,
            c.TrustLevel,
            c.State,
            c.CreatedAtUtc,
            IsOnline: true)).ToList();
        return summaries;
    }

    public async Task<PeerContactDetailReadModel?> GetContactDetailAsync(PublicIdentityId ownerId, PublicIdentityId contactId, CancellationToken ct = default)
    {
        var contact = await _contactRepo.GetByPeerIdAsync(ownerId, contactId, ct).ConfigureAwait(false);
        if (contact == null)
        {
            return null;
        }

        return new PeerContactDetailReadModel(
            contact.RemotePeerId,
            contact.Nickname.Value,
            contact.TrustLevel,
            contact.State,
            contact.PrimaryPublicKey ?? IdentityKey.FromSpan(new byte[32]),
            contact.RegisteredDevices.ToList(),
            contact.CreatedAtUtc,
            contact.LastSeenAtUtc ?? contact.CreatedAtUtc);
    }
}

public sealed class InMemoryGroupInvitationQueryService : IGroupInvitationQueryService
{
    private readonly InMemoryGroupInvitationRepository _invitationRepo;
    private readonly PublicIdentityId _recipientId;

    public InMemoryGroupInvitationQueryService(InMemoryGroupInvitationRepository invitationRepo, PublicIdentityId recipientId)
    {
        _invitationRepo = invitationRepo ?? throw new ArgumentNullException(nameof(invitationRepo));
        _recipientId = recipientId;
    }

    public async Task<IReadOnlyList<GroupInvitationSummaryReadModel>> GetPendingInvitationsAsync(CancellationToken ct = default)
    {
        var all = await _invitationRepo.GetAllPendingAsync(ct).ConfigureAwait(false);
        var list = all
            .Where(inv => inv.TargetGroupChannelId != default && inv.Status == GroupInvitationStatus.Pending)
            .Select(inv => new GroupInvitationSummaryReadModel(
                inv.InvitationId,
                inv.TargetGroupChannelId,
                inv.GroupName,
                inv.InviterId,
                inv.ExpiresAtUnixMs,
                inv.Status))
            .ToList();
        return list;
    }
}
