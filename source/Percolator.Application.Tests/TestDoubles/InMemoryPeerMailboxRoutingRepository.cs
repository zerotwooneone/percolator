using System.Collections.Concurrent;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk.Profiles;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.TestDoubles;

public sealed class InMemoryPeerMailboxRoutingRepository : IPeerMailboxRoutingRepository
{
    private readonly ConcurrentDictionary<PublicIdentityId, PeerMailboxRoutingCapability> _routes = new();

    public Task SaveAsync(PeerMailboxRoutingCapability capability, CancellationToken ct = default)
    {
        _routes[capability.PeerId] = capability;
        return Task.CompletedTask;
    }

    public Task<PeerMailboxRoutingCapability?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        _routes.TryGetValue(peerId, out var capability);
        return Task.FromResult(capability);
    }

    public Task<IReadOnlyList<PeerMailboxRoutingCapability>> GetRoutesForPeerAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        _routes.TryGetValue(peerId, out var capability);
        IReadOnlyList<PeerMailboxRoutingCapability> list = capability != null ? [capability] : [];
        return Task.FromResult(list);
    }

    public Task<PeerMailboxRoutingCapability?> GetBestRouteAsync(PublicIdentityId peerId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        _routes.TryGetValue(peerId, out var capability);
        return Task.FromResult(capability);
    }

    public Task RecordBackpressureAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, TimeSpan retryAfter, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task RecordSuccessAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
