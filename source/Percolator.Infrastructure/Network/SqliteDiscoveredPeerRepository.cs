using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using Percolator.Network.ValueObjects;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace Percolator.Infrastructure.Network;

public sealed class SqliteDiscoveredPeerRepository : IDiscoveredPeerRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteDiscoveredPeerRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public Task<DiscoveredPeer?> GetByDiscoveryKeyAsync(DiscoveryKey key, CancellationToken cancellationToken = default)
    {
        return this.GetByDiscoveryKeyInternalAsync(key, cancellationToken);
    }

    public Task<DiscoveredPeer?> GetByPublicKeyHashAsync(PublicKeyHash pkh, CancellationToken cancellationToken = default)
    {
        return GetByPublicKeyHashInternalAsync(pkh, cancellationToken);
    }

    public Task UpsertAsync(DiscoveredPeer entity, CancellationToken cancellationToken = default)
    {
        return this.UpsertInternalAsync(entity, cancellationToken);
    }

    public Task<IEnumerable<DiscoveredPeer>> GetCandidatesAsync(DateTimeOffset seenSince, CancellationToken cancellationToken = default)
    {
        return GetCandidatesInternalAsync(seenSince, cancellationToken);
    }

    public Task<PeerRoutingProfile> PromoteToRoutingProfileAsync(DiscoveredPeer provisional, NetworkPeerId id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(provisional.PromoteToRoutingProfile(id, DateTimeOffset.UtcNow));
    }

    public Task<PeerRoutingProfile> BindIdentityAsync(PeerRoutingProfile aggregate, NetworkPeerId id, CancellationToken cancellationToken = default)
    {
        aggregate.BindIdentity(id);
        return Task.FromResult(aggregate);
    }

    // Private helpers
    private async Task UpsertInternalAsync(DiscoveredPeer entity, CancellationToken ct)
    {
        var row = await _db.DiscoveredPeers.FirstOrDefaultAsync(x => x.DiscoveryKey == entity.DiscoveryKey.Value, ct);
        if (row is null)
        {
            row = new DiscoveredPeerDbo
            {
                DiscoveryKey = entity.DiscoveryKey.Value,
                PublicKeyHash = entity.IdentityPublicKeyHash?.ToArray(),
                FirstSeenUtc = entity.FirstSeenUtc,
                LastSeenUtc = entity.LastSeenUtc,
                Source = (int)entity.Source,
                Confidence = entity.Confidence,
                BoundPeerId = entity.BoundPeerId?.Value
            };
            _db.DiscoveredPeers.Add(row);
        }
        else
        {
            row.PublicKeyHash = entity.IdentityPublicKeyHash?.ToArray();
            row.LastSeenUtc = entity.LastSeenUtc;
            row.Source = (int)entity.Source;
            row.Confidence = entity.Confidence;
            row.BoundPeerId = entity.BoundPeerId?.Value;
        }

        // Merge endpoints
        foreach (var ep in entity.Endpoints)
        {
            var host = ep.EndPoint.Host;
            var port = ep.EndPoint.Port;
            var existingEp = await _db.DiscoveredPeerEndpoints.FirstOrDefaultAsync(e => e.DiscoveryKey == entity.DiscoveryKey.Value && e.Host == host && e.Port == port, ct);
            if (existingEp is null)
            {
                _db.DiscoveredPeerEndpoints.Add(new DiscoveredPeerEndpointDbo
                {
                    DiscoveryKey = entity.DiscoveryKey.Value,
                    Host = host,
                    Port = port,
                    FirstSeenUtc = ep.LastSeen,
                    LastSeenUtc = ep.LastSeen
                });
            }
            else
            {
                if (ep.LastSeen > existingEp.LastSeenUtc)
                {
                    existingEp.LastSeenUtc = ep.LastSeen;
                }
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task<DiscoveredPeer?> GetByDiscoveryKeyInternalAsync(DiscoveryKey key, CancellationToken ct)
    {
        var row = await _db.DiscoveredPeers.AsNoTracking().FirstOrDefaultAsync(x => x.DiscoveryKey == key.Value, ct);
        if (row is null) return null;

        var pkh = row.PublicKeyHash is null ? (PublicKeyHash?)null : PublicKeyHash.FromBytesOwned(row.PublicKeyHash);
        var dp = DiscoveredPeer.Create(new DiscoveryKey(row.DiscoveryKey), pkh, row.FirstSeenUtc);
        if (row.LastSeenUtc > row.FirstSeenUtc)
        {
            dp.RecordDiscovery((DiscoverySource)row.Source, row.LastSeenUtc);
        }

        // Hydrate endpoints
        var eps = await _db.DiscoveredPeerEndpoints.AsNoTracking()
            .Where(e => e.DiscoveryKey == row.DiscoveryKey)
            .ToListAsync(ct);
        foreach (var e in eps)
        {
            dp.ObserveEndpoint(new GrpcEndPoint(new DnsEndPoint(e.Host, e.Port), e.LastSeenUtc), e.LastSeenUtc);
        }
        return dp;
    }

    private async Task<DiscoveredPeer?> GetByPublicKeyHashInternalAsync(PublicKeyHash pkh, CancellationToken ct)
    {
        var row = await _db.DiscoveredPeers.AsNoTracking().FirstOrDefaultAsync(x => x.PublicKeyHash != null && x.PublicKeyHash.SequenceEqual(pkh.ToArray()), ct);
        if (row is null) return null;
        var dp = DiscoveredPeer.Create(new DiscoveryKey(row.DiscoveryKey), PublicKeyHash.FromBytesOwned(row.PublicKeyHash!), row.FirstSeenUtc);
        if (row.LastSeenUtc > row.FirstSeenUtc)
        {
            dp.RecordDiscovery((DiscoverySource)row.Source, row.LastSeenUtc);
        }
        return dp;
    }

    private async Task<IEnumerable<DiscoveredPeer>> GetCandidatesInternalAsync(DateTimeOffset seenSince, CancellationToken ct)
    {
        var all = await _db.DiscoveredPeers.AsNoTracking().ToListAsync(ct);
        var rows = all.Where(x => x.LastSeenUtc >= seenSince).ToList();

        var candidates = rows.Select(row =>
        {
            var pkh = row.PublicKeyHash is null ? (PublicKeyHash?)null : PublicKeyHash.FromBytesOwned(row.PublicKeyHash);
            var dp = DiscoveredPeer.Create(new DiscoveryKey(row.DiscoveryKey), pkh, row.FirstSeenUtc);
            if (row.LastSeenUtc > row.FirstSeenUtc)
            {
                dp.RecordDiscovery((DiscoverySource)row.Source, row.LastSeenUtc);
            }
            return dp;
        }).ToList();

        return candidates
            .OrderByDescending(c => c.LastSeenUtc)
            .ThenByDescending(c => c.Confidence)
            .ThenBy(c => c.DiscoveryKey.Value)
            .ToList();
    }
}
