using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Network.ValueObjects;

namespace Percolator.Network;

public interface IDiscoveredPeerRepository
{
    Task<DiscoveredPeer?> GetByDiscoveryKeyAsync(DiscoveryKey key, CancellationToken cancellationToken = default);
    Task<DiscoveredPeer?> GetByPublicKeyHashAsync(PublicKeyHash pkh, CancellationToken cancellationToken = default);
    Task UpsertAsync(DiscoveredPeer entity, CancellationToken cancellationToken = default);
    Task<IEnumerable<DiscoveredPeer>> GetCandidatesAsync(DateTimeOffset seenSince, CancellationToken cancellationToken = default);
    Task<PeerRoutingProfile> PromoteToRoutingProfileAsync(DiscoveredPeer provisional, PeerId id, CancellationToken cancellationToken = default);
    Task<PeerRoutingProfile> BindIdentityAsync(PeerRoutingProfile aggregate, PeerId id, CancellationToken cancellationToken = default);
}
