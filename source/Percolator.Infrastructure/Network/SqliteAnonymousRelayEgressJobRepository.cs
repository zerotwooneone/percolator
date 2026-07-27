using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using Percolator.Network.Egress;
using Percolator.Network.ValueObjects;

namespace Percolator.Infrastructure.Network;

public sealed class SqliteAnonymousRelayEgressJobRepository : IAnonymousRelayEgressJobRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteAnonymousRelayEgressJobRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<AnonymousRelayEgressJob?> GetByIdAsync(EgressJobId jobId, CancellationToken cancellationToken)
    {
        var dbo = await _db.AnonymousRelayEgressJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.JobId == jobId.Value, cancellationToken);

        if (dbo is null)
            return null;

        return ToDomain(dbo);
    }

    public async Task AddAsync(AnonymousRelayEgressJob job, CancellationToken cancellationToken)
    {
        var dbo = ToDbo(job);
        _db.AnonymousRelayEgressJobs.Add(dbo);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(AnonymousRelayEgressJob job, CancellationToken cancellationToken)
    {
        var existing = await _db.AnonymousRelayEgressJobs
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

        _db.AnonymousRelayEgressJobs.Update(existing);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(EgressJobId jobId, CancellationToken cancellationToken)
    {
        var existing = await _db.AnonymousRelayEgressJobs
            .FirstOrDefaultAsync(j => j.JobId == jobId.Value, cancellationToken);

        if (existing is not null)
        {
            _db.AnonymousRelayEgressJobs.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private static AnonymousRelayEgressJob ToDomain(AnonymousRelayEgressJobDbo dbo)
    {
        return new AnonymousRelayEgressJob(
            new EgressJobId(dbo.JobId),
            new NetworkPeerId(dbo.RelayPeerId),
            NetworkPayloadBytes.FromBytesOwned(dbo.PayloadBytes),
            dbo.NextAttemptUtc)
        {
            // Set private fields via reflection or add a factory method
            // For now, we'll need to update the aggregate to support reconstruction
        };
    }

    private static AnonymousRelayEgressJobDbo ToDbo(AnonymousRelayEgressJob job)
    {
        return new AnonymousRelayEgressJobDbo
        {
            JobId = job.JobId.Value,
            RelayPeerId = job.RelayPeerId.Value,
            PayloadBytes = job.PayloadBytes.ToArray(),
            AttemptCount = job.Attempts.Value,
            NextAttemptUtc = job.NextAttemptUtc,
            IsSent = job.IsSent,
            IsPermanentlyFailed = job.IsPermanentlyFailed
        };
    }
}
