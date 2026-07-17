using Microsoft.EntityFrameworkCore;
using Percolator.Application.Network;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Network;

/// <summary>
/// SQLite implementation of IRelayPeerQueries.
/// </summary>
public sealed class SqliteRelayPeerQueries : IRelayPeerQueries
{
    private readonly PercolatorDbContext _db;

    public SqliteRelayPeerQueries(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<IEnumerable<RelayConnection>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // Join RelayLinkDbo with SelfIdentityKnownPeerDbo to find relay connections
        // RelayLinkDbo: PeerId -> RelayPeerId (which peers use which relays)
        // SelfIdentityKnownPeerDbo: SelfIdentityId -> PeerId (which selfId owns which peers)
        // SelfIdentityDbo: SelfIdentityId -> DeviceId (device ID for each self identity)
        var query = from relay in _db.PeerRoutingRelays
                    join knownPeer in _db.SelfIdentityKnownPeers on relay.PeerId equals knownPeer.PeerId
                    join selfIdentity in _db.SelfIdentities on knownPeer.SelfIdentityId equals selfIdentity.Id
                    select new RelayConnection(knownPeer.SelfIdentityId, relay.RelayPeerId, selfIdentity.DeviceId);

        return await query
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
