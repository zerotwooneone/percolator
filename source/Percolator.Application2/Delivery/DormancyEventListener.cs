using Percolator.Application2.Delivery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Events;

namespace Percolator.Application2.Delivery;

public sealed class DormancyEventListener
{
    private readonly IOutboxRepository _repository;
    private readonly IDateTimeProvider _timeProvider;

    public DormancyEventListener(IOutboxRepository repository, IDateTimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task HandleAsync(IdentityDisabledEvent @event, CancellationToken ct = default)
    {
        var pendingJobs = await _repository.GetPendingJobsForOwnerAsync(@event.IdentityId, ct);
        foreach (var job in pendingJobs)
        {
            job.PauseForDormancy(_timeProvider);
            await _repository.SaveAsync(job, ct);
        }
    }
}
