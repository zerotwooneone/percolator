using System.Collections.Concurrent;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

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
    private readonly ConcurrentDictionary<PublicIdentityId, PeerMailboxRoutingCapability> _storage = new();

    public Task SaveAsync(PeerMailboxRoutingCapability capability, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        _storage[capability.PeerId] = capability;
        return Task.CompletedTask;
    }

    public Task<PeerMailboxRoutingCapability?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        _storage.TryGetValue(peerId, out var capability);
        return Task.FromResult(capability);
    }
}
