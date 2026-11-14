using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Discovery.Ports;

/// <summary>
/// Port for querying a remote peer/relay over network transport for nearby peers.
/// </summary>
public interface INearbyDiscoveryQueryClientPort
{
    Task<IReadOnlyList<PeerDiscoveryDescriptor>> SendNearbyQueryAsync(
        PublicIdentityId requesterId,
        PublicIdentityId targetPeerOrRelayId,
        DiscoveryResultLimit limit,
        CancellationToken ct = default);
}
