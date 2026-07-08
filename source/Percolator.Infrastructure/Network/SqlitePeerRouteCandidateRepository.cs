using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Infrastructure.Network;

public sealed class SqlitePeerRouteCandidateRepository : IPeerRouteCandidateRepository
{
    private readonly PercolatorDbContext _db;

    public SqlitePeerRouteCandidateRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task UpsertAsync(PeerRouteCandidate candidate, CancellationToken cancellationToken = default)
    {
        var candidateRelayHostPeerId = candidate.RelayHostPeerId?.Value;
        var existing = await _db.PeerRouteCandidates
            .FirstOrDefaultAsync(c =>
                c.SelfIdentityId == candidate.SelfIdentityId &&
                c.RemotePeerId == candidate.RemotePeerId.Value &&
                c.RouteKind == (int)candidate.RouteKind &&
                c.EndpointHost == candidate.EndpointHost &&
                c.EndpointPort == candidate.EndpointPort &&
                c.RelayHostPeerId == candidateRelayHostPeerId,
                cancellationToken);

        if (existing is null)
        {
            var dbo = new PeerRouteCandidateDbo
            {
                SelfIdentityId = candidate.SelfIdentityId,
                RemotePeerId = candidate.RemotePeerId.Value,
                RouteKind = (int)candidate.RouteKind,
                EndpointHost = candidate.EndpointHost,
                EndpointPort = candidate.EndpointPort,
                RelayHostPeerId = candidate.RelayHostPeerId?.Value,
                ObservedAtUtc = candidate.ObservedAtUtc,
                LastAttemptAtUtc = candidate.LastAttemptAtUtc,
                LastSuccessAtUtc = candidate.LastSuccessAtUtc,
                AttemptCount = candidate.AttemptCount,
                LastError = candidate.LastError,
                Source = candidate.Source
            };
            _db.PeerRouteCandidates.Add(dbo);
        }
        else
        {
            existing.LastAttemptAtUtc = candidate.LastAttemptAtUtc;
            existing.LastSuccessAtUtc = candidate.LastSuccessAtUtc;
            existing.AttemptCount = candidate.AttemptCount;
            existing.LastError = candidate.LastError;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PeerRouteCandidate>> GetCandidatesAsync(uint selfIdentityId, Percolator.Network.PeerId remotePeerId, CancellationToken cancellationToken = default)
    {
        var dbos = await _db.PeerRouteCandidates
            .AsNoTracking()
            .Where(c => c.SelfIdentityId == selfIdentityId && c.RemotePeerId == remotePeerId.Value)
            .ToListAsync(cancellationToken);

        return dbos.Select(MapToDomain).ToList();
    }

    public async Task<IReadOnlyList<PeerRouteCandidate>> GetAllCandidatesAsync(uint selfIdentityId, CancellationToken cancellationToken = default)
    {
        var dbos = await _db.PeerRouteCandidates
            .AsNoTracking()
            .Where(c => c.SelfIdentityId == selfIdentityId)
            .ToListAsync(cancellationToken);

        return dbos.Select(MapToDomain).ToList();
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var dbo = await _db.PeerRouteCandidates.FindAsync(new object[] { id }, cancellationToken);
        if (dbo is not null)
        {
            _db.PeerRouteCandidates.Remove(dbo);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task PruneAsync(int selfIdentityId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var cutoff = nowUtc.AddDays(-14);
        var toPrune = _db.PeerRouteCandidates
            .Where(c =>
                c.SelfIdentityId == selfIdentityId &&
                c.LastSuccessAtUtc == null &&
                c.ObservedAtUtc < cutoff);

        _db.PeerRouteCandidates.RemoveRange(toPrune);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static PeerRouteCandidate MapToDomain(PeerRouteCandidateDbo dbo)
    {
        return new PeerRouteCandidate
        {
            Id = dbo.Id,
            SelfIdentityId = dbo.SelfIdentityId,
            RemotePeerId = new Percolator.Network.PeerId(dbo.RemotePeerId),
            RouteKind = (RouteKind)dbo.RouteKind,
            EndpointHost = dbo.EndpointHost,
            EndpointPort = dbo.EndpointPort,
            RelayHostPeerId = dbo.RelayHostPeerId.HasValue ? new Percolator.Network.PeerId(dbo.RelayHostPeerId.Value) : null,
            ObservedAtUtc = dbo.ObservedAtUtc,
            LastAttemptAtUtc = dbo.LastAttemptAtUtc,
            LastSuccessAtUtc = dbo.LastSuccessAtUtc,
            AttemptCount = dbo.AttemptCount,
            LastError = dbo.LastError,
            Source = dbo.Source
        };
    }
}
