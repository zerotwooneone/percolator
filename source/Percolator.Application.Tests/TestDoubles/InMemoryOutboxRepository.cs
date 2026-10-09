using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class InMemoryOutboxRepository : IOutboxRepository
{
    private readonly Dictionary<Guid, OutboxJob> _jobs = [];

    public IReadOnlyCollection<OutboxJob> AllJobs => _jobs.Values;

    public Task<OutboxJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        _jobs.TryGetValue(id, out var job);
        return Task.FromResult(job);
    }

    public Task<IReadOnlyList<OutboxJob>> GetPendingJobsForOwnerAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        IReadOnlyList<OutboxJob> result = _jobs.Values
            .Where(j => j.OwnerIdentityId == ownerId && j.Status == OutboxStatus.Pending)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<OutboxJob>> GetDuePendingJobsAsync(DateTimeOffset asOfUtc, int limit = 50, CancellationToken ct = default)
    {
        IReadOnlyList<OutboxJob> result = _jobs.Values
            .Where(j => j.Status == OutboxStatus.Pending && j.NextAttemptAtUtc <= asOfUtc)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAsync(OutboxJob job, CancellationToken ct = default)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }
}
