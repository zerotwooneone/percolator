using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

/// <summary>
/// In-memory peer mailbox routing store; returns no routes so lookups behave as if no relay route exists.
/// </summary>
public sealed class FakePeerMailboxRoutingRepository : IPeerMailboxRoutingRepository
{
    private readonly Dictionary<PublicIdentityId, List<PeerMailboxRoutingCapability>> _routes = new();

    public Task SaveAsync(PeerMailboxRoutingCapability capability, CancellationToken ct = default)
    {
        if (!_routes.TryGetValue(capability.PeerId, out var list))
        {
            list = new List<PeerMailboxRoutingCapability>();
            _routes[capability.PeerId] = list;
        }

        list.RemoveAll(c => c.RoutingToken == capability.RoutingToken);
        list.Add(capability);
        return Task.CompletedTask;
    }

    public Task<PeerMailboxRoutingCapability?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        PeerMailboxRoutingCapability? result = _routes.TryGetValue(peerId, out var list) ? list.FirstOrDefault() : null;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<PeerMailboxRoutingCapability>> GetRoutesForPeerAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        IReadOnlyList<PeerMailboxRoutingCapability> result = _routes.TryGetValue(peerId, out var list)
            ? list.ToList()
            : Array.Empty<PeerMailboxRoutingCapability>();
        return Task.FromResult(result);
    }

    public Task<PeerMailboxRoutingCapability?> GetBestRouteAsync(PublicIdentityId peerId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        PeerMailboxRoutingCapability? result = _routes.TryGetValue(peerId, out var list)
            ? list.FirstOrDefault(c => c.IsAvailable(nowUtc))
            : null;
        return Task.FromResult(result);
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
