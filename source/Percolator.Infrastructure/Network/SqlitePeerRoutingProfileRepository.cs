using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using Percolator.Network.ValueObjects;
using System.Net;

namespace Percolator.Infrastructure.Network;

public sealed class SqlitePeerRoutingProfileRepository : IPeerRoutingProfileRepository
{
    private readonly PercolatorDbContext _db;

    public SqlitePeerRoutingProfileRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    private async Task<PeerRoutingProfile?> GetByIdInternalAsync(PeerId id, CancellationToken ct)
    {
        var row = await _db.PeerRoutingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.PeerId == id.Value, ct);
        if (row is null)
        {
            return null;
        }
        var profile = new PeerRoutingProfile();
        profile.BindIdentity(new PeerId(row.PeerId));
        if (row.DirectMessagePublicKey is not null)
        {
            profile.SetIdentityPublicKey(IdentityPublicKey.FromBytesOwned(row.DirectMessagePublicKey));
        }
        if (row.ReachabilityLastChangeUtc is DateTimeOffset ts)
        {
            profile.RecordReachability((ReachabilityStatus)row.ReachabilityStatus, ts);
        }
        // Hydrate endpoints
        var eps = await _db.PeerRoutingGrpcEndPoints.AsNoTracking()
            .Where(e => e.PeerId == row.PeerId)
            .ToListAsync(ct);
        foreach (var e in eps)
        {
            profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint(e.Host, e.Port), e.LastSeenUtc), e.LastSeenUtc);
        }
        // Hydrate relays
        var relayRows = await _db.PeerRoutingRelays.AsNoTracking()
            .Where(r => r.PeerId == row.PeerId)
            .ToListAsync(ct);
        foreach (var r in relayRows)
        {
            profile.AddOrRefreshRelay(new PeerId(r.RelayPeerId), r.LastSeenUtc);
        }
        return profile;
    }

    private async Task UpsertInternalAsync(PeerRoutingProfile aggregate, CancellationToken ct)
    {
        if (aggregate.Id is null)
        {
            throw new InvalidOperationException("PeerRoutingProfile must be bound to a PeerId before persisting.");
        }

        var row = await _db.PeerRoutingProfiles.FirstOrDefaultAsync(p => p.PeerId == aggregate.Id.Value, ct);
        if (row is null)
        {
            row = new PeerRoutingProfileDbo
            {
                PeerId = aggregate.Id.Value,
                ReachabilityStatus = (int)aggregate.Reachability.Status,
                ReachabilityLastChangeUtc = aggregate.Reachability.LastChangeUtc == DateTimeOffset.MinValue ? null : aggregate.Reachability.LastChangeUtc,
                DirectMessagePublicKey = aggregate.IdentityPublicKey?.ToArray()
            };
            _db.PeerRoutingProfiles.Add(row);
        }
        else
        {
            row.ReachabilityStatus = (int)aggregate.Reachability.Status;
            row.ReachabilityLastChangeUtc = aggregate.Reachability.LastChangeUtc == DateTimeOffset.MinValue ? null : aggregate.Reachability.LastChangeUtc;
            row.DirectMessagePublicKey = aggregate.IdentityPublicKey?.ToArray();
        }

        // Merge endpoints
        foreach (var ep in aggregate.Endpoints)
        {
            var host = ep.EndPoint.Host;
            var port = ep.EndPoint.Port;
            var existing = await _db.PeerRoutingGrpcEndPoints.FirstOrDefaultAsync(e => e.PeerId == row.PeerId && e.Host == host && e.Port == port, ct);
            if (existing is null)
            {
                _db.PeerRoutingGrpcEndPoints.Add(new GrpcEndPointRoutingDbo
                {
                    PeerId = row.PeerId,
                    Host = host,
                    Port = port,
                    LastSeenUtc = ep.LastSeen
                });
            }
            else if (ep.LastSeen > existing.LastSeenUtc)
            {
                existing.LastSeenUtc = ep.LastSeen;
            }
        }

        // Sync relays: upsert present ones and remove missing
        var existingRelays = await _db.PeerRoutingRelays.Where(r => r.PeerId == row.PeerId).ToListAsync(ct);
        // Upsert
        foreach (var relay in aggregate.Relays)
        {
            var found = existingRelays.FirstOrDefault(x => x.RelayPeerId == relay.RelayPeerId.Value);
            if (found is null)
            {
                _db.PeerRoutingRelays.Add(new RelayLinkDbo
                {
                    PeerId = row.PeerId,
                    RelayPeerId = relay.RelayPeerId.Value,
                    LastSeenUtc = relay.Freshness.LastSeenUtc
                });
            }
            else if (relay.Freshness.LastSeenUtc > found.LastSeenUtc)
            {
                found.LastSeenUtc = relay.Freshness.LastSeenUtc;
            }
        }
        // Remove missing
        var toRemove = existingRelays.Where(db => !aggregate.Relays.Any(ar => ar.RelayPeerId.Value == db.RelayPeerId)).ToList();
        if (toRemove.Count > 0)
        {
            _db.PeerRoutingRelays.RemoveRange(toRemove);
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<IEnumerable<PeerRoutingProfile>> GetStaleInternalAsync(DateTimeOffset threshold, CancellationToken ct)
    {
        var all = await _db.PeerRoutingProfiles
            .AsNoTracking()
            .ToListAsync(ct);

        var rows = all.Where(p => p.ReachabilityLastChangeUtc.HasValue && p.ReachabilityLastChangeUtc.Value <= threshold).ToList();

        var list = new List<PeerRoutingProfile>(rows.Count);
        foreach (var row in rows)
        {
            var profile = new PeerRoutingProfile();
            profile.BindIdentity(new PeerId(row.PeerId));
            if (row.ReachabilityLastChangeUtc is DateTimeOffset ts)
            {
                profile.RecordReachability((ReachabilityStatus)row.ReachabilityStatus, ts);
            }
            list.Add(profile);
        }
        return list;
    }

    private async Task<PeerRoutingProfile?> GetByPublicKeyInternalAsync(IdentityPublicKey pk, CancellationToken ct)
    {
        var all = await _db.PeerRoutingProfiles
            .AsNoTracking()
            .ToListAsync(ct);

        var match = all.FirstOrDefault(r => r.DirectMessagePublicKey != null && r.DirectMessagePublicKey.AsSpan().SequenceEqual(pk.Span));
        if (match is null)
        {
            return null;
        }
        var profile = new PeerRoutingProfile();
        profile.BindIdentity(new PeerId(match.PeerId));
        profile.SetIdentityPublicKey(IdentityPublicKey.FromBytesOwned(match.DirectMessagePublicKey!));
        if (match.ReachabilityLastChangeUtc is DateTimeOffset ts)
        {
            profile.RecordReachability((ReachabilityStatus)match.ReachabilityStatus, ts);
        }
        return profile;
    }

    public Task<PeerRoutingProfile?> GetByIdAsync(PeerId id, CancellationToken cancellationToken = default)
    {
        return GetByIdInternalAsync(id, cancellationToken);
    }

    public Task UpsertAsync(PeerRoutingProfile aggregate, CancellationToken cancellationToken = default)
    {
        return UpsertInternalAsync(aggregate, cancellationToken);
    }

    public Task<PeerRoutingProfile?> GetByPublicKeyAsync(IdentityPublicKey pk, CancellationToken cancellationToken = default)
    {
        return GetByPublicKeyInternalAsync(pk, cancellationToken);
    }

    public Task<IEnumerable<PeerRoutingProfile>> GetStaleAsync(DateTimeOffset threshold, CancellationToken cancellationToken = default)
    {
        return GetStaleInternalAsync(threshold, cancellationToken);
    }
}
