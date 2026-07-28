using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;
using Percolator.Network.Egress;
using Percolator.Network.ValueObjects;

namespace Percolator.Infrastructure.Network.Egress;

public sealed class SqliteRelayEgressJobRepository : IRelayEgressJobRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteRelayEgressJobRepository(PercolatorDbContext db) => _db = db;

    public async Task<RelayEgressJob?> GetByIdAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var dbo = await _db.RelayEgressJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.JobId == jobId, cancellationToken);

        if (dbo == null)
            return null;

        return new RelayEgressJob(
            dbo.JobId,
            new NetworkPeerId(dbo.DestinationPeerId),
            dbo.PayloadBytes,
            DateTimeOffset.FromUnixTimeMilliseconds(dbo.NextAttemptUtc));
    }

    public async Task SaveAsync(RelayEgressJob job, CancellationToken cancellationToken)
    {
        var dbo = await _db.RelayEgressJobs
            .FirstOrDefaultAsync(e => e.JobId == job.JobId, cancellationToken);

        if (dbo == null)
        {
            dbo = new RelayEgressJobDbo
            {
                JobId = job.JobId,
                DestinationPeerId = job.DestinationPeerId.Value,
                PayloadBytes = job.PayloadBytes,
                AttemptCount = job.AttemptCount,
                NextAttemptUtc = job.NextAttemptUtc.ToUnixTimeMilliseconds()
            };
            _db.RelayEgressJobs.Add(dbo);
        }
        else
        {
            dbo.AttemptCount = job.AttemptCount;
            dbo.NextAttemptUtc = job.NextAttemptUtc.ToUnixTimeMilliseconds();
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var dbo = await _db.RelayEgressJobs
            .FirstOrDefaultAsync(e => e.JobId == jobId, cancellationToken);

        if (dbo != null)
        {
            _db.RelayEgressJobs.Remove(dbo);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
