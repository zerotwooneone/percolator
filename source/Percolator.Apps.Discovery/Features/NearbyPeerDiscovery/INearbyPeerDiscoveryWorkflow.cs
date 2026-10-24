using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;

/// <summary>
/// Workflow coordinating Step 1 (Query nearby peers) and Step 2 (Connect to discovered peer via host IPeerConnectionService).
/// </summary>
public interface INearbyPeerDiscoveryWorkflow
{
    /// <summary>
    /// Step 1: Queries target peer or relay for candidate descriptors.
    /// Never touches or consumes One-Time PreKeys (OPKs).
    /// </summary>
    Task<IReadOnlyList<PeerDiscoveryDescriptor>> QueryNearbyPeersAsync(
        PublicIdentityId targetPeerOrRelayId,
        DiscoveryResultLimit limit,
        CancellationToken ct = default);

    /// <summary>
    /// Step 2: Connects to candidate peer via host IPeerConnectionService.
    /// </summary>
    Task<DomainResult<PeerConnectionResult>> ConnectToDiscoveredPeerAsync(
        PublicIdentityId localOwnerId,
        DeviceId localDeviceId,
        PeerDiscoveryDescriptor candidate,
        CancellationToken ct = default);

    /// <summary>
    /// Composite workflow: Queries target node, then initiates handshakes with all discovered candidates.
    /// Fault-tolerant: failure on one candidate does not abort handshakes with other candidates.
    /// </summary>
    Task<DiscoveredPeerConnectionResult> DiscoverAndConnectAsync(
        PublicIdentityId localOwnerId,
        DeviceId localDeviceId,
        PublicIdentityId targetPeerOrRelayId,
        DiscoveryResultLimit limit,
        CancellationToken ct = default);
}
