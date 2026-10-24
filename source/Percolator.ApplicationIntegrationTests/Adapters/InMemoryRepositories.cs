using System.Collections.Concurrent;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

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
    private readonly ConcurrentDictionary<PublicIdentityId, (byte[] PrivateKey, IdentityKey PublicKey)> _keys = new();

    public void RegisterKey(PublicIdentityId id, byte[] privateKey, IdentityKey publicKey)
    {
        _keys[id] = (privateKey, publicKey);
    }

    public Task<byte[]?> GetIdentityPrivateKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        return Task.FromResult(_keys.TryGetValue(identityId, out var entry) ? entry.PrivateKey : null);
    }

    public Task<IdentityKey?> GetIdentityPublicKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        return Task.FromResult(_keys.TryGetValue(identityId, out var entry) ? entry.PublicKey : null);
    }
}

public sealed class InMemoryPrivatePreKeyStore : IPrivatePreKeyStore
{
    private readonly ConcurrentDictionary<(PublicIdentityId, DeviceId), EphemeralPrivateKey> _signedPreKeys = new();
    private readonly ConcurrentDictionary<(PublicIdentityId, DeviceId, uint), EphemeralPrivateKey> _oneTimePreKeys = new();

    public Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        _signedPreKeys.TryGetValue((ownerId, deviceId), out var key);
        return Task.FromResult(key);
    }

    public Task<EphemeralPrivateKey?> TryConsumeOneTimePreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        uint keyId,
        CancellationToken cancellationToken = default)
    {
        if (_oneTimePreKeys.TryRemove((ownerId, deviceId, keyId), out var key))
        {
            return Task.FromResult<EphemeralPrivateKey?>(key);
        }

        return Task.FromResult<EphemeralPrivateKey?>(null);
    }

    public Task StoreSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        CancellationToken cancellationToken = default)
    {
        _signedPreKeys[(ownerId, deviceId)] = privateKey;
        return Task.CompletedTask;
    }

    public Task StoreOneTimePreKeysPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        IEnumerable<(uint KeyId, EphemeralPrivateKey Key)> keys,
        CancellationToken cancellationToken = default)
    {
        foreach (var (keyId, key) in keys)
        {
            _oneTimePreKeys[(ownerId, deviceId, keyId)] = key;
        }

        return Task.CompletedTask;
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
    private readonly ConcurrentDictionary<PublicIdentityId, PeerMailboxRoutingCapability> _capabilities = new();

    public Task SaveAsync(PeerMailboxRoutingCapability capability, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        _capabilities[capability.PeerId] = capability;
        return Task.CompletedTask;
    }

    public Task<PeerMailboxRoutingCapability?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        _capabilities.TryGetValue(peerId, out var capability);
        return Task.FromResult(capability);
    }
}
