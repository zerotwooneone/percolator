using System.Collections.Concurrent;
using Percolator.Application2.Routing;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Infrastructure;

public sealed class TestPeerReachabilityService : IPeerReachabilityService
{
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch;
    private readonly PublicIdentityId _localIdentityId;
    private readonly ConcurrentDictionary<PublicIdentityId, PublicIdentityId> _peerHomeRelays = new();

    public TestPeerReachabilityService(
        InMemoryLoopbackNetworkSwitch networkSwitch,
        PublicIdentityId localIdentityId)
    {
        _networkSwitch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
        _localIdentityId = localIdentityId;
    }

    public void SetHomeRelay(PublicIdentityId peerId, PublicIdentityId relayId)
    {
        _peerHomeRelays[peerId] = relayId;
    }

    public ValueTask<bool> IsDirectlyReachableAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        bool hasStream = _networkSwitch.HasActiveStreamForPeer(_localIdentityId, peerId);
        return ValueTask.FromResult(hasStream);
    }

    public ValueTask<PublicIdentityId?> GetHomeRelayAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        if (_peerHomeRelays.TryGetValue(peerId, out var relayId))
        {
            return ValueTask.FromResult<PublicIdentityId?>(relayId);
        }

        return ValueTask.FromResult<PublicIdentityId?>(null);
    }
}
