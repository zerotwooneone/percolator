using Percolator.Application2.Delivery.Ports;
using Percolator.Domain.Common;

namespace Percolator.Application2.Delivery;

public sealed class OutboxWorker
{
    private readonly IOutboxRepository _repository;
    private readonly ITransportDispatcher _dispatcher;
    private readonly IDateTimeProvider _timeProvider;

    public OutboxWorker(
        IOutboxRepository repository,
        ITransportDispatcher dispatcher,
        IDateTimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<int> ProcessBatchAsync(int batchSize = 50, CancellationToken ct = default)
    {
        var dueJobs = await _repository.GetDuePendingJobsAsync(_timeProvider.UtcNow, batchSize, ct);
        int processedCount = 0;

        foreach (var job in dueJobs)
        {
            if (ct.IsCancellationRequested) break;

            job.MarkInFlight();
            await _repository.SaveAsync(job, ct);

            var sendResult = await _dispatcher.SendAsync(job, ct);
            if (sendResult.IsSuccess)
            {
                job.MarkDelivered(_timeProvider);
            }
            else
            {
                var nextAttempt = OutboxRetryPolicy.CalculateNextAttempt(job.RetryCount, _timeProvider.UtcNow);
                job.RecordFailure(nextAttempt);
            }

            await _repository.SaveAsync(job, ct);
            processedCount++;
        }

        return processedCount;
    }
}
