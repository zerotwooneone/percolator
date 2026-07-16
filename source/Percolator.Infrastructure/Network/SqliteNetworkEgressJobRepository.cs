using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using Percolator.Network.Egress;
using Percolator.Network.ValueObjects;

namespace Percolator.Infrastructure.Network;

public sealed class SqliteNetworkEgressJobRepository : INetworkEgressJobRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteNetworkEgressJobRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<EgressJobId> CreateAsync(NetworkEgressJob job, CancellationToken cancellationToken = default)
    {
        var dbo = new NetworkEgressJobDbo
        {
            JobId = 0, // EF Core will generate this
            DestinationPeerId = job.DestinationPeerId.Value,
            RoutePreference = (int)job.RoutePreference,
            PayloadType = (int)job.PayloadType,
            PayloadBytes = job.PayloadBytes.ToArray(),
            AttemptCount = job.AttemptCount,
            NextAttemptUtc = job.NextAttemptUtc,
            IsSent = job.IsSent,
            IsPermanentlyFailed = job.IsPermanentlyFailed
        };
        _db.NetworkEgressJobs.Add(dbo);
        await _db.SaveChangesAsync(cancellationToken);
        
        return new EgressJobId(dbo.JobId);
    }

    public async Task SaveAsync(NetworkEgressJob job, CancellationToken cancellationToken = default)
    {
        var existing = await _db.NetworkEgressJobs
            .FirstOrDefaultAsync(j => j.JobId == job.JobId.Value, cancellationToken);

        if (existing is null)
        {
            throw new InvalidOperationException($"Job with ID {job.JobId.Value} not found. Use CreateAsync for new jobs.");
        }

        // Update existing job
        existing.DestinationPeerId = job.DestinationPeerId.Value;
        existing.RoutePreference = (int)job.RoutePreference;
        existing.PayloadType = (int)job.PayloadType;
        existing.PayloadBytes = job.PayloadBytes.ToArray();
        existing.AttemptCount = job.AttemptCount;
        existing.NextAttemptUtc = job.NextAttemptUtc;
        existing.IsSent = job.IsSent;
        existing.IsPermanentlyFailed = job.IsPermanentlyFailed;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<NetworkEgressJob?> GetByIdAsync(EgressJobId jobId, CancellationToken cancellationToken = default)
    {
        var dbo = await _db.NetworkEgressJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.JobId == jobId.Value, cancellationToken);

        if (dbo is null)
        {
            return null;
        }

        return MapToAggregate(dbo);
    }

    public async Task<IReadOnlyList<NetworkEgressJob>> GetPendingJobsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var dbos = await _db.NetworkEgressJobs
            .AsNoTracking()
            .Where(j => !j.IsSent && !j.IsPermanentlyFailed && j.NextAttemptUtc <= nowUtc)
            .OrderBy(j => j.NextAttemptUtc)
            .ToListAsync(cancellationToken);

        return dbos.Select(MapToAggregate).ToList();
    }

    public async Task DeleteAsync(EgressJobId jobId, CancellationToken cancellationToken = default)
    {
        var dbo = await _db.NetworkEgressJobs
            .FirstOrDefaultAsync(j => j.JobId == jobId.Value, cancellationToken);

        if (dbo is not null)
        {
            _db.NetworkEgressJobs.Remove(dbo);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private static NetworkEgressJob MapToAggregate(NetworkEgressJobDbo dbo)
    {
        return new NetworkEgressJob(
            new EgressJobId(dbo.JobId),
            new NetworkPeerId(dbo.DestinationPeerId),
            (RoutePreference)dbo.RoutePreference,
            (PayloadType)dbo.PayloadType,
            NetworkPayloadBytes.FromBytesOwned(dbo.PayloadBytes),
            dbo.NextAttemptUtc);
    }
}
