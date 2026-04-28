using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;

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
        var existing = await _db.PeerRouteCandidates
            .FirstOrDefaultAsync(c =>
                c.SelfIdentityId == candidate.SelfIdentityId &&
                c.RemotePeerId == candidate.RemotePeerId.Value &&
                c.RouteKind == (int)candidate.RouteKind &&
                c.EndpointHost == candidate.EndpointHost &&
                c.EndpointPort == candidate.EndpointPort &&
                c.RelayHostPeerId == candidate.RelayHostPeerId,
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
                RelayHostPeerId = candidate.RelayHostPeerId,
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

    public async Task<IReadOnlyList<PeerRouteCandidate>> GetCandidatesAsync(int selfIdentityId, PeerId remotePeerId, CancellationToken cancellationToken = default)
    {
        var dbos = await _db.PeerRouteCandidates
            .AsNoTracking()
            .Where(c => c.SelfIdentityId == selfIdentityId && c.RemotePeerId == remotePeerId.Value)
            .ToListAsync(cancellationToken);

        return dbos.Select(MapToDomain).ToList();
    }

    public async Task<IReadOnlyList<PeerRouteCandidate>> GetAllCandidatesAsync(int selfIdentityId, CancellationToken cancellationToken = default)
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

    private static PeerRouteCandidate MapToDomain(PeerRouteCandidateDbo dbo)
    {
        return new PeerRouteCandidate
        {
            Id = dbo.Id,
            SelfIdentityId = dbo.SelfIdentityId,
            RemotePeerId = new PeerId(dbo.RemotePeerId),
            RouteKind = (RouteKind)dbo.RouteKind,
            EndpointHost = dbo.EndpointHost,
            EndpointPort = dbo.EndpointPort,
            RelayHostPeerId = dbo.RelayHostPeerId,
            ObservedAtUtc = dbo.ObservedAtUtc,
            LastAttemptAtUtc = dbo.LastAttemptAtUtc,
            LastSuccessAtUtc = dbo.LastSuccessAtUtc,
            AttemptCount = dbo.AttemptCount,
            LastError = dbo.LastError,
            Source = dbo.Source
        };
    }
}
