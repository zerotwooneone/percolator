using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.RelayDirectoryHosting;

/// <summary>
/// Port for hosting peer registrations on a relay or individual discoverable node.
/// </summary>
public interface IRelayPeerDirectory
{
    RelayDiscoveryPolicy Policy { get; set; }

    ValueTask<DomainResult> RegisterPeerAsync(
        RelayPeerRegistration registration,
        CancellationToken ct = default);

    ValueTask<DomainResult> UpdateVisibilityAsync(
        PublicIdentityId peerId,
        PeerDiscoveryVisibility visibility,
        CancellationToken ct = default);

    ValueTask<RelayPeerRegistration?> GetPeerAsync(
        PublicIdentityId peerId,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<RelayPeerRegistration>> GetDiscoverablePeersAsync(
        int? limit = null,
        CancellationToken ct = default);
}
