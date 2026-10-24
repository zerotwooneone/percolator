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
                string.Equals(r.RelayEndpoint, capability.RelayEndpoint, StringComparison.OrdinalIgnoreCase) ||
                (r.RelayEndpoint == null && capability.RelayEndpoint == null));

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

    public Task RecordBackpressureAsync(PublicIdentityId peerId, string relayEndpoint, TimeSpan retryAfter, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_peerRoutes.TryGetValue(peerId, out var routes))
            {
                var idx = routes.FindIndex(r => string.Equals(r.RelayEndpoint, relayEndpoint, StringComparison.OrdinalIgnoreCase));
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

    public Task RecordSuccessAsync(PublicIdentityId peerId, string relayEndpoint, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_peerRoutes.TryGetValue(peerId, out var routes))
            {
                var idx = routes.FindIndex(r => string.Equals(r.RelayEndpoint, relayEndpoint, StringComparison.OrdinalIgnoreCase));
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
