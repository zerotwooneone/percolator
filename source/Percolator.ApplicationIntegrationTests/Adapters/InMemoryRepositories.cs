using System.Collections.Concurrent;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class InMemoryChatMessageRepository : IChatMessageRepository
{
    private readonly ConcurrentDictionary<PayloadId, ChatMessage> _storage = new();

    public IReadOnlyCollection<ChatMessage> All => _storage.Values.ToList();

    public Task AddAsync(ChatMessage record, CancellationToken ct = default)
    {
        _storage[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<ChatMessage?> GetByIdAsync(PayloadId id, CancellationToken ct = default)
    {
        _storage.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<ChatMessage>> GetByChannelIdAsync(ChannelId channelId, CancellationToken ct = default)
    {
        var messages = _storage.Values
            .Where(m => m.ChannelId == channelId)
            .OrderBy(m => m.TimestampUnixMs)
            .ToList();
        return Task.FromResult<IReadOnlyList<ChatMessage>>(messages);
    }

    public Task UpdateAsync(ChatMessage record, CancellationToken ct = default)
    {
        _storage[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<int> PurgeExpiredMessagesAsync(ulong currentTimestampUnixMs, CancellationToken ct = default)
    {
        int purgedCount = 0;
        foreach (var kvp in _storage)
        {
            if (kvp.Value.IsExpired(currentTimestampUnixMs))
            {
                if (_storage.TryRemove(kvp.Key, out _))
                {
                    purgedCount++;
                }
            }
        }

        return Task.FromResult(purgedCount);
    }
}

public sealed class InMemoryChannelRepository : IChannelRepository
{
    private readonly ConcurrentDictionary<ChannelId, DirectChannel> _directChannels = new();
    private readonly ConcurrentDictionary<ChannelId, GroupChannel> _groupChannels = new();

    public IReadOnlyCollection<DirectChannel> AllDirect => _directChannels.Values.ToList();
    public IReadOnlyCollection<GroupChannel> AllGroup => _groupChannels.Values.ToList();

    public Task<DirectChannel?> GetDirectByIdAsync(ChannelId id, CancellationToken cancellationToken = default)
    {
        _directChannels.TryGetValue(id, out var channel);
        return Task.FromResult(channel);
    }

    public Task<DirectChannel?> GetDirectByPeerAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default)
    {
        var channel = _directChannels.Values.FirstOrDefault(c =>
            (c.OwnerIdentityId == ownerId && c.RemotePeerId == remotePeerId) ||
            (c.OwnerIdentityId == remotePeerId && c.RemotePeerId == ownerId));
        return Task.FromResult(channel);
    }

    public Task<GroupChannel?> GetGroupByIdAsync(ChannelId id, CancellationToken cancellationToken = default)
    {
        _groupChannels.TryGetValue(id, out var channel);
        return Task.FromResult(channel);
    }

    public Task<IReadOnlyList<GroupChannel>> GetAllGroupsForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
    {
        var list = _groupChannels.Values
            .Where(g => g.Members.Any(m => m.Id == ownerId))
            .ToList();
        return Task.FromResult<IReadOnlyList<GroupChannel>>(list);
    }

    public Task SaveDirectAsync(DirectChannel channel, CancellationToken cancellationToken = default)
    {
        _directChannels[channel.Id] = channel;
        return Task.CompletedTask;
    }

    public Task SaveGroupAsync(GroupChannel channel, CancellationToken cancellationToken = default)
    {
        _groupChannels[channel.Id] = channel;
        return Task.CompletedTask;
    }

}

public sealed class InMemoryOutboxRepository : IOutboxRepository
{
    private readonly ConcurrentDictionary<Guid, OutboxJob> _jobs = new();

    public IReadOnlyCollection<OutboxJob> AllJobs => _jobs.Values.ToList();

    public Task<OutboxJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        _jobs.TryGetValue(id, out var job);
        return Task.FromResult(job);
    }

    public Task<IReadOnlyList<OutboxJob>> GetPendingJobsForOwnerAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        IReadOnlyList<OutboxJob> result = _jobs.Values
            .Where(j => j.OwnerIdentityId == ownerId && j.Status == OutboxStatus.Pending)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<OutboxJob>> GetDuePendingJobsAsync(DateTimeOffset asOfUtc, int limit = 50, CancellationToken ct = default)
    {
        IReadOnlyList<OutboxJob> result = _jobs.Values
            .Where(j => j.Status == OutboxStatus.Pending && j.NextAttemptAtUtc <= asOfUtc)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<OutboxJob>> ClaimDuePendingJobsAsync(int batchSize, string workerInstanceId, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var due = _jobs.Values
            .Where(j => j.Status == OutboxStatus.Pending && j.NextAttemptAtUtc <= now)
            .Take(batchSize)
            .ToList();

        foreach (var job in due)
        {
            job.MarkInFlight();
        }

        return Task.FromResult<IReadOnlyList<OutboxJob>>(due);
    }

    public Task SaveAsync(OutboxJob job, CancellationToken ct = default)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

}

public sealed class InMemoryPeerContactRepository : IPeerContactRepository
{
    private readonly ConcurrentDictionary<(PublicIdentityId Owner, PublicIdentityId Peer), PeerContact> _contacts = new();

    public Task<PeerContact?> GetByPeerIdAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default)
    {
        _contacts.TryGetValue((ownerId, remotePeerId), out var contact);
        return Task.FromResult(contact);
    }

    public Task<IReadOnlyList<PeerContact>> GetAllForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PeerContact> list = _contacts.Values.Where(c => c.OwnerIdentityId == ownerId).ToList();
        return Task.FromResult(list);
    }

    public Task SaveAsync(PeerContact contact, CancellationToken cancellationToken = default)
    {
        _contacts[(contact.OwnerIdentityId, contact.RemotePeerId)] = contact;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryRatchetSessionRepository : IRatchetSessionRepository
{
    private readonly ConcurrentDictionary<(PublicIdentityId Owner, PublicIdentityId Peer, DeviceId Device), DirectRatchetSession> _sessions = new();

    public Task<DirectRatchetSession?> GetSessionAsync(
        PublicIdentityId ownerId,
        PublicIdentityId peerId,
        DeviceId peerDeviceId,
        CancellationToken cancellationToken = default)
    {
        _sessions.TryGetValue((ownerId, peerId, peerDeviceId), out var session);
        return Task.FromResult(session);
    }

    public Task SaveSessionAsync(DirectRatchetSession session, CancellationToken cancellationToken = default)
    {
        _sessions[(session.OwnerIdentityId, session.RemotePeerId, session.RemoteDeviceId)] = session;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryPendingHandshakeRepository : IPendingHandshakeRepository
{
    private readonly ConcurrentDictionary<(PublicIdentityId Recipient, PublicIdentityId Sender), InboundHandshakeEnvelope> _handshakes = new();

    public Task SavePendingHandshakeAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        InboundHandshakeEnvelope envelope,
        CancellationToken ct = default)
    {
        _handshakes[(recipientId, senderId)] = envelope;
        return Task.CompletedTask;
    }

    public Task<InboundHandshakeEnvelope?> GetPendingHandshakeAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        CancellationToken ct = default)
    {
        _handshakes.TryGetValue((recipientId, senderId), out var envelope);
        return Task.FromResult(envelope);
    }

    public Task DeletePendingHandshakeAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        CancellationToken ct = default)
    {
        _handshakes.TryRemove((recipientId, senderId), out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryLocalIdentityKeyStore : ILocalIdentityKeyStore
{
    private readonly ConcurrentDictionary<PublicIdentityId, (EphemeralPrivateKey PrivateKey, IdentityKey PublicKey, ProfileKey ProfileKey)> _keys = new();

    public void RegisterKey(PublicIdentityId id, EphemeralPrivateKey privateKey, IdentityKey publicKey, ProfileKey? profileKey = null)
    {
        // Clone on store: callers may Dispose() their instance (e.g. maintenance
        // `using`), which must not zeroize the stored copy — the sqlite store
        // copies bytes on every access.
        _keys[id] = (EphemeralPrivateKey.FromSpan(privateKey.Span), publicKey, profileKey ?? ProfileKey.Generate());
    }

    public Task<EphemeralPrivateKey?> GetIdentityPrivateKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        return Task.FromResult(_keys.TryGetValue(identityId, out var entry)
            ? EphemeralPrivateKey.FromSpan(entry.PrivateKey.Span)
            : null);
    }

    public Task<IdentityKey?> GetIdentityPublicKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        return Task.FromResult(_keys.TryGetValue(identityId, out var entry) ? entry.PublicKey : null);
    }

    public Task<ProfileKey?> GetProfileKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        return Task.FromResult(_keys.TryGetValue(identityId, out var entry) ? entry.ProfileKey : null);
    }

    public Task SaveIdentityKeysAsync(
        PublicIdentityId identityId,
        EphemeralPrivateKey privateKey,
        IdentityKey publicKey,
        ProfileKey profileKey,
        CancellationToken ct = default)
    {
        _keys[identityId] = (EphemeralPrivateKey.FromSpan(privateKey.Span), publicKey, profileKey);
        return Task.CompletedTask;
    }

    public Task<ProfileKey> RotateProfileKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        if (!_keys.TryGetValue(identityId, out var entry))
        {
            throw new InvalidOperationException($"No identity keys stored for '{identityId.Value}'.");
        }

        var rotated = ProfileKey.Generate();
        _keys[identityId] = (entry.PrivateKey, entry.PublicKey, rotated);
        return Task.FromResult(rotated);
    }

    public Task DeleteIdentityKeysAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        _keys.TryRemove(identityId, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryPrivatePreKeyStore : IPrivatePreKeyStore
{
    private readonly ConcurrentDictionary<(PublicIdentityId, DeviceId, uint), StoredSignedPreKey> _signedPreKeys = new();
    private readonly ConcurrentDictionary<(PublicIdentityId, DeviceId), uint> _currentSignedPreKeyIds = new();
    private readonly ConcurrentDictionary<(PublicIdentityId, DeviceId, uint), OneTimePreKeyMaterial> _oneTimePreKeys = new();

    public Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        SignedPreKeyId? signedPreKeyId = null,
        CancellationToken cancellationToken = default)
    {
        uint keyId;
        if (signedPreKeyId.HasValue)
        {
            keyId = signedPreKeyId.Value.Value;
        }
        else if (!_currentSignedPreKeyIds.TryGetValue((ownerId, deviceId), out keyId))
        {
            return Task.FromResult<EphemeralPrivateKey?>(null);
        }

        _signedPreKeys.TryGetValue((ownerId, deviceId, keyId), out var stored);
        // Shared generation — return an owned clone; callers Dispose() after use
        // (handshake responder) and must not zeroize the retained generation.
        return Task.FromResult(stored == null ? null : EphemeralPrivateKey.FromSpan(stored.PrivateKey.Span));
    }

    public Task<EphemeralPrivateKey?> TryConsumeOneTimePreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        uint keyId,
        CancellationToken cancellationToken = default)
    {
        if (_oneTimePreKeys.TryRemove((ownerId, deviceId, keyId), out var material))
        {
            return Task.FromResult<EphemeralPrivateKey?>(material.PrivateKey);
        }

        return Task.FromResult<EphemeralPrivateKey?>(null);
    }

    public Task<StoredSignedPreKey?> GetSignedPreKeyAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        StoredSignedPreKey? stored = null;
        if (_currentSignedPreKeyIds.TryGetValue((ownerId, deviceId), out var keyId))
        {
            _signedPreKeys.TryGetValue((ownerId, deviceId, keyId), out stored);
        }
        return Task.FromResult(stored == null ? null : stored with
        {
            PrivateKey = EphemeralPrivateKey.FromSpan(stored.PrivateKey.Span)
        });
    }

    public Task<SignedPreKeyId> StoreSignedPreKeyAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        DhPublicKey publicKey,
        DeviceLinkProof signature,
        CancellationToken cancellationToken = default)
    {
        var nextKeyId = _signedPreKeys.Keys
            .Where(k => k.Item1 == ownerId && k.Item2 == deviceId)
            .Select(k => k.Item3)
            .DefaultIfEmpty(0u)
            .Max() + 1u;

        _signedPreKeys[(ownerId, deviceId, nextKeyId)] =
            new StoredSignedPreKey(new SignedPreKeyId(nextKeyId), EphemeralPrivateKey.FromSpan(privateKey.Span), publicKey, signature, DateTimeOffset.UtcNow);
        _currentSignedPreKeyIds[(ownerId, deviceId)] = nextKeyId;
        return Task.FromResult(new SignedPreKeyId(nextKeyId));
    }

    public Task<int> PruneSignedPreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        DateTimeOffset olderThanUtc,
        int minRetainedCount = 3,
        CancellationToken cancellationToken = default)
    {
        _currentSignedPreKeyIds.TryGetValue((ownerId, deviceId), out var currentKeyId);

        var generations = _signedPreKeys
            .Where(kv => kv.Key.Item1 == ownerId && kv.Key.Item2 == deviceId)
            .Select(kv => (KeyId: kv.Key.Item3, Record: kv.Value))
            .OrderByDescending(g => g.KeyId)
            .ToList();

        var candidates = generations
            .Where(g => g.KeyId != currentKeyId && g.Record.CreatedAtUtc < olderThanUtc)
            .ToList();
        var retained = generations.Count - candidates.Count;
        var toDelete = candidates
            .OrderBy(g => g.KeyId)
            .Take(Math.Max(0, candidates.Count - Math.Max(0, minRetainedCount - retained)))
            .ToList();

        foreach (var g in toDelete)
        {
            _signedPreKeys.TryRemove((ownerId, deviceId, g.KeyId), out _);
        }
        return Task.FromResult(toDelete.Count);
    }

    public Task<int> CountAvailableOneTimePreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var count = _oneTimePreKeys.Keys.Count(k => k.Item1 == ownerId && k.Item2 == deviceId);
        return Task.FromResult(count);
    }

    public Task StoreOneTimePreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        IEnumerable<OneTimePreKeyMaterial> keys,
        CancellationToken cancellationToken = default)
    {
        foreach (var material in keys)
        {
            // Clone the private half — publishers Dispose() generated materials
            // after storing (allocation gate path), which would otherwise zero
            // every hosted OPK private key in this double.
            _oneTimePreKeys[(ownerId, deviceId, material.KeyId)] = new OneTimePreKeyMaterial(
                material.KeyId,
                EphemeralPrivateKey.FromSpan(material.PrivateKey.Span),
                material.PublicKey);
        }

        return Task.CompletedTask;
    }

    public Task<uint> GetNextOneTimePreKeyIdAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var maxId = _oneTimePreKeys.Keys
            .Where(k => k.Item1 == ownerId && k.Item2 == deviceId)
            .Select(k => (uint?)k.Item3)
            .Max();

        return Task.FromResult((maxId ?? 0u) + 1u);
    }
}

public sealed class InMemoryGroupSenderKeyRepository : IGroupSenderKeyRepository
{
    private readonly ConcurrentDictionary<(ChannelId, PublicIdentityId, DeviceId), GroupSenderKeyRatchet> _ratchets = new();

    public Task<GroupSenderKeyRatchet?> GetSenderKeyRatchetAsync(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        CancellationToken ct = default)
    {
        _ratchets.TryGetValue((channelId, authorId, authorDeviceId), out var ratchet);
        return Task.FromResult(ratchet);
    }

    public Task SaveSenderKeyRatchetAsync(GroupSenderKeyRatchet ratchet, CancellationToken ct = default)
    {
        _ratchets[(ratchet.ChannelId, ratchet.AuthorId, ratchet.AuthorDeviceId)] = ratchet;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryGroupReceiverSessionRepository : IGroupReceiverSessionRepository
{
    private readonly ConcurrentDictionary<(ChannelId, PublicIdentityId, DeviceId), GroupReceiverSession> _sessions = new();

    public Task<GroupReceiverSession?> GetReceiverSessionAsync(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        CancellationToken ct = default)
    {
        _sessions.TryGetValue((channelId, authorId, authorDeviceId), out var session);
        return Task.FromResult(session);
    }

    public Task SaveReceiverSessionAsync(GroupReceiverSession session, CancellationToken ct = default)
    {
        _sessions[(session.ChannelId, session.AuthorId, session.AuthorDeviceId)] = session;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryGroupInvitationRepository : IGroupInvitationRepository
{
    private readonly ConcurrentDictionary<InvitationId, PendingGroupInvitation> _invitations = new();

    public Task AddAsync(PendingGroupInvitation invitation, CancellationToken ct = default)
    {
        _invitations[invitation.InvitationId] = invitation;
        return Task.CompletedTask;
    }

    public Task<PendingGroupInvitation?> GetByIdAsync(InvitationId invitationId, CancellationToken ct = default)
    {
        _invitations.TryGetValue(invitationId, out var inv);
        return Task.FromResult(inv);
    }

    public Task UpdateAsync(PendingGroupInvitation invitation, CancellationToken ct = default)
    {
        _invitations[invitation.InvitationId] = invitation;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PendingGroupInvitation>> GetAllPendingAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PendingGroupInvitation> list = _invitations.Values.ToList();
        return Task.FromResult(list);
    }

    public Task<int> PurgeExpiredAsync(ulong currentTimestampUnixMs, CancellationToken ct = default)
    {
        int count = 0;
        foreach (var kvp in _invitations)
        {
            if (kvp.Value.CheckExpiration(currentTimestampUnixMs))
            {
                if (_invitations.TryRemove(kvp.Key, out _)) count++;
            }
        }
        return Task.FromResult(count);
    }
}

public sealed class InMemoryIngressFilterService : IIngressFilterService
{
    private readonly HashSet<PublicIdentityId> _blockedIdentities = [];

    public void Block(PublicIdentityId id) => _blockedIdentities.Add(id);
    public void Unblock(PublicIdentityId id) => _blockedIdentities.Remove(id);

    public ValueTask<DomainResult> CheckIngressAllowedAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        CancellationToken ct = default)
    {
        if (_blockedIdentities.Contains(senderId))
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError("IDENTITY_BLOCKED", "Sender is blocked.")));
        }

        return ValueTask.FromResult(DomainResult.Success());
    }
}

public sealed class InMemoryPeerProfileRepository : IPeerProfileRepository
{
    private readonly ConcurrentDictionary<PublicIdentityId, PeerProfileRecord> _profiles = new();
    private readonly ConcurrentDictionary<PublicIdentityId, List<PeerProfileSourceSnapshot>> _snapshots = new();
    private readonly object _lock = new();

    public Task SaveAsync(PeerProfileRecord record, CancellationToken ct = default)
    {
        _profiles[record.PeerId] = record;
        return Task.CompletedTask;
    }

    public Task<PeerProfileRecord?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        _profiles.TryGetValue(peerId, out var record);
        return Task.FromResult(record);
    }

    public Task SaveSourceSnapshotAsync(PeerProfileSourceSnapshot snapshot, int maxSnapshotsPerPeer = 5, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_lock)
        {
            var list = _snapshots.GetOrAdd(snapshot.PeerId, _ => new List<PeerProfileSourceSnapshot>());
            var existingIndex = list.FindIndex(s => s.SourceEndpoint == snapshot.SourceEndpoint);
            if (existingIndex >= 0)
            {
                list[existingIndex] = snapshot;
            }
            else
            {
                list.Add(snapshot);
                if (list.Count > maxSnapshotsPerPeer)
                {
                    list.Sort((a, b) => a.LastObservedAtUtc.CompareTo(b.LastObservedAtUtc));
                    while (list.Count > maxSnapshotsPerPeer)
                    {
                        list.RemoveAt(0);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PeerProfileSourceSnapshot>> GetSourceSnapshotsAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_snapshots.TryGetValue(peerId, out var list))
            {
                return Task.FromResult<IReadOnlyList<PeerProfileSourceSnapshot>>(list.ToList());
            }

            return Task.FromResult<IReadOnlyList<PeerProfileSourceSnapshot>>(Array.Empty<PeerProfileSourceSnapshot>());
        }
    }
}

public sealed class InMemoryPeerMailboxRoutingRepository : IPeerMailboxRoutingRepository
{
    private const int MaxRoutesPerPeer = 5;
    private readonly ConcurrentDictionary<PublicIdentityId, List<PeerMailboxRoutingCapability>> _peerRoutes = new();
    private readonly object _lock = new();

    public Task SaveAsync(PeerMailboxRoutingCapability capability, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capability);

        lock (_lock)
        {
            var routes = _peerRoutes.GetOrAdd(capability.PeerId, _ => new List<PeerMailboxRoutingCapability>());
            var existingIndex = routes.FindIndex(r =>
                (r.RelayEndpoint == null && capability.RelayEndpoint == null) ||
                (r.RelayEndpoint != null && capability.RelayEndpoint != null && r.RelayEndpoint.Equals(capability.RelayEndpoint)));

            if (existingIndex >= 0)
            {
                routes[existingIndex] = capability;
            }
            else
            {
                routes.Add(capability);
                if (routes.Count > MaxRoutesPerPeer)
                {
                    routes.Sort((a, b) => (a.LastSuccessAtUtc ?? a.LastObservedAtUtc).CompareTo(b.LastSuccessAtUtc ?? b.LastObservedAtUtc));
                    while (routes.Count > MaxRoutesPerPeer)
                    {
                        routes.RemoveAt(0);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<PeerMailboxRoutingCapability?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_peerRoutes.TryGetValue(peerId, out var routes) && routes.Count > 0)
            {
                var latest = routes.OrderByDescending(r => r.LastSuccessAtUtc ?? r.LastObservedAtUtc).FirstOrDefault();
                return Task.FromResult(latest);
            }

            return Task.FromResult<PeerMailboxRoutingCapability?>(null);
        }
    }

    public Task<IReadOnlyList<PeerMailboxRoutingCapability>> GetRoutesForPeerAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_peerRoutes.TryGetValue(peerId, out var routes))
            {
                return Task.FromResult<IReadOnlyList<PeerMailboxRoutingCapability>>(routes.ToList());
            }

            return Task.FromResult<IReadOnlyList<PeerMailboxRoutingCapability>>(Array.Empty<PeerMailboxRoutingCapability>());
        }
    }

    public Task<PeerMailboxRoutingCapability?> GetBestRouteAsync(PublicIdentityId peerId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_peerRoutes.TryGetValue(peerId, out var routes) && routes.Count > 0)
            {
                var best = routes
                    .Where(r => r.IsAvailable(nowUtc))
                    .OrderByDescending(r => r.LastSuccessAtUtc ?? r.LastObservedAtUtc)
                    .FirstOrDefault();

                return Task.FromResult(best);
            }

            return Task.FromResult<PeerMailboxRoutingCapability?>(null);
        }
    }

    public Task RecordBackpressureAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, TimeSpan retryAfter, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_peerRoutes.TryGetValue(peerId, out var routes))
            {
                var idx = routes.FindIndex(r => r.RelayEndpoint != null && r.RelayEndpoint.Equals(relayEndpoint));
                if (idx >= 0)
                {
                    var existing = routes[idx];
                    var updated = existing with
                    {
                        BackpressureCooldownUntilUtc = nowUtc.Add(retryAfter)
                    };
                    routes[idx] = updated;
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task RecordSuccessAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_peerRoutes.TryGetValue(peerId, out var routes))
            {
                var idx = routes.FindIndex(r => r.RelayEndpoint != null && r.RelayEndpoint.Equals(relayEndpoint));
                if (idx >= 0)
                {
                    var existing = routes[idx];
                    var updated = existing with
                    {
                        LastSuccessAtUtc = nowUtc,
                        BackpressureCooldownUntilUtc = null
                    };
                    routes[idx] = updated;
                }
            }
        }

        return Task.CompletedTask;
    }

}

public sealed class InMemoryRelayRegistrationRepository : IRelayRegistrationRepository
{
    private readonly ConcurrentDictionary<PublicIdentityId, RelayRegistration> _registrations = new();

    public Task<RelayRegistration?> GetByIdAsync(PublicIdentityId relayIdentityId, CancellationToken ct = default)
    {
        _registrations.TryGetValue(relayIdentityId, out var registration);
        return Task.FromResult(registration);
    }

    public Task<IReadOnlyList<RelayRegistration>> GetAllAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<RelayRegistration>>(_registrations.Values.ToList());
    }

    public Task<IReadOnlyList<RelayRegistration>> GetByOwnerIdentityAsync(PublicIdentityId ownerIdentityId, CancellationToken ct = default)
    {
        var matches = _registrations.Values
            .Where(r => r.Bindings.Any(b => b.OwnerIdentityId == ownerIdentityId))
            .ToList();
        return Task.FromResult<IReadOnlyList<RelayRegistration>>(matches);
    }

    public Task SaveAsync(RelayRegistration registration, CancellationToken ct = default)
    {
        _registrations[registration.Id] = registration;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(PublicIdentityId relayIdentityId, CancellationToken ct = default)
    {
        _registrations.TryRemove(relayIdentityId, out _);
        return Task.CompletedTask;
    }
}
