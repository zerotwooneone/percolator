using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;

/// <summary>
/// Public read model representing a discoverable candidate peer.
/// By virtue of being in discovery results, the peer is guaranteed to have passed admission gates
/// (authenticated handshake + active Signed PreKey).
/// </summary>
public sealed record PeerDiscoveryDescriptor(
    PublicIdentityId PeerId,
    DeliveryRoute Route,
    RelayEndpoint? RelayEndpoint = null,
    BlindedRoutingToken? RoutingToken = null,
    DeliveryToken? DeliveryToken = null);
