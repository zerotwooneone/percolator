using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;

/// <summary>
/// Result of an end-to-end DiscoverAndConnect workflow, summarizing successfully established sessions
/// and granular per-peer failures.
/// </summary>
public sealed record DiscoveredPeerConnectionResult(
    IReadOnlyList<DiscoveredPeerConnectionSummary> ConnectedPeers,
    IReadOnlyList<DiscoveredPeerConnectionFailure> Failures);

public sealed record DiscoveredPeerConnectionSummary(
    PublicIdentityId PeerId,
    DeviceId DeviceId);

public sealed record DiscoveredPeerConnectionFailure(
    PublicIdentityId PeerId,
    DomainError Error);
