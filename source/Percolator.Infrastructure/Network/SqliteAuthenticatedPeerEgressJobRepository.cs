using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using Percolator.Network.Egress;
using Percolator.Network.ValueObjects;

namespace Percolator.Infrastructure.Network;

public sealed class SqliteAuthenticatedPeerEgressJobRepository : IAuthenticatedPeerEgressJobRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteAuthenticatedPeerEgressJobRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<AuthenticatedPeerEgressJob?> GetByIdAsync(EgressJobId jobId, CancellationToken cancellationToken)
    {
        var dbo = await _db.AuthenticatedPeerEgressJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.JobId == jobId.Value, cancellationToken);

        if (dbo is null)
            return null;

        return ToDomain(dbo);
    }

    public async Task AddAsync(AuthenticatedPeerEgressJob job, CancellationToken cancellationToken)
    {
        var dbo = ToDbo(job);
        _db.AuthenticatedPeerEgressJobs.Add(dbo);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(AuthenticatedPeerEgressJob job, CancellationToken cancellationToken)
    {
        var existing = await _db.AuthenticatedPeerEgressJobs
            .FirstOrDefaultAsync(j => j.JobId == job.JobId.Value, cancellationToken);

        if (existing is null)
        {
            await AddAsync(job, cancellationToken);
            return;
        }

        existing.AttemptCount = job.Attempts.Value;
        existing.NextAttemptUtc = job.NextAttemptUtc;
        existing.IsSent = job.IsSent;
        existing.IsPermanentlyFailed = job.IsPermanentlyFailed;

        _db.AuthenticatedPeerEgressJobs.Update(existing);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(EgressJobId jobId, CancellationToken cancellationToken)
    {
        var existing = await _db.AuthenticatedPeerEgressJobs
            .FirstOrDefaultAsync(j => j.JobId == jobId.Value, cancellationToken);

        if (existing is not null)
        {
            _db.AuthenticatedPeerEgressJobs.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private static AuthenticatedPeerEgressJob ToDomain(AuthenticatedPeerEgressJobDbo dbo)
    {
        return new AuthenticatedPeerEgressJob(
            new EgressJobId(dbo.JobId),
            new NetworkPeerId(dbo.DestinationPeerId),
            (RoutePreference)dbo.RoutePreference,
            NetworkPayloadBytes.FromBytesOwned(dbo.PayloadBytes),
            dbo.NextAttemptUtc)
        {
            // Set private fields via reflection or add a factory method
            // For now, we'll need to update the aggregate to support reconstruction
        };
    }

    private static AuthenticatedPeerEgressJobDbo ToDbo(AuthenticatedPeerEgressJob job)
    {
        return new AuthenticatedPeerEgressJobDbo
        {
            JobId = job.JobId.Value,
            DestinationPeerId = job.DestinationPeerId.Value,
            RoutePreference = (int)job.RoutePreference,
            PayloadBytes = job.PayloadBytes.ToArray(),
            AttemptCount = job.Attempts.Value,
            NextAttemptUtc = job.NextAttemptUtc,
            IsSent = job.IsSent,
            IsPermanentlyFailed = job.IsPermanentlyFailed
        };
    }
}
