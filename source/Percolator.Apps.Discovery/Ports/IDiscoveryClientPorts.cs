using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;

namespace Percolator.Apps.Discovery.Ports;

/// <summary>
/// Domain-level port for initiating an X3DH handshake session.
/// (Implemented by HandshakeService in the application layer).
/// </summary>
public interface IDiscoveryHandshakePort
{
    ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(
        PublicIdentityId ownerId,
        DeviceId ownerDeviceId,
        PreKeyBundle remoteBundle,
        CancellationToken ct = default);
}

/// <summary>
/// Port for querying a remote peer/relay over network transport for nearby peers.
/// </summary>
public interface INearbyDiscoveryQueryClientPort
{
    Task<IReadOnlyList<PeerDiscoveryDescriptor>> SendNearbyQueryAsync(
        PublicIdentityId targetPeerOrRelayId,
        DiscoveryResultLimit limit,
        CancellationToken ct = default);
}
