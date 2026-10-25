using Percolator.Application2IntegrationTests.Builders;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Apps.Discovery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class LoopbackNearbyDiscoveryQueryClient : INearbyDiscoveryQueryClientPort
{
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch;

    public LoopbackNearbyDiscoveryQueryClient(InMemoryLoopbackNetworkSwitch networkSwitch)
    {
        _networkSwitch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
    }

    public async Task<IReadOnlyList<PeerDiscoveryDescriptor>> SendNearbyQueryAsync(
        PublicIdentityId targetPeerOrRelayId,
        DiscoveryResultLimit limit,
        CancellationToken ct = default)
    {
        RelayNode? relay = null;
        if (!_networkSwitch.TryGetRelay(targetPeerOrRelayId, out relay))
        {
            relay = _networkSwitch.AllRelays.FirstOrDefault(r => r.RelayIdentityId == targetPeerOrRelayId);
        }

        relay ??= _networkSwitch.AllRelays.FirstOrDefault();
        if (relay is null)
        {
            return Array.Empty<PeerDiscoveryDescriptor>();
        }

        var discoverable = await relay.Directory.GetDiscoverablePeersAsync(limit.Value, ct).ConfigureAwait(false);
        var relayEndpoint = RelayEndpoint.FromHostAndPort("localhost", relay.Port.Value);

        return discoverable
            .Select(p =>
            {
                relay.TryGetClientTokens(p.PeerId, out var tokens);
                return new PeerDiscoveryDescriptor(
                    p.PeerId,
                    DeliveryRoute.RelayedOneToOne,
                    relayEndpoint,
                    tokens.RoutingToken.Equals(default) ? null : tokens.RoutingToken,
                    tokens.DeliveryToken == null ? null : tokens.DeliveryToken);
            })
            .ToList();
    }
}
