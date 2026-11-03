namespace Percolator.Infrastructure.Maintenance;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Events;
using Percolator.Application2.Ports;
using Percolator.Infrastructure.Persistence;

public sealed class OutboxRetentionPruner : BackgroundService
{
    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;
    private readonly IEventPublisher _eventPublisher;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pruneInterval;
    private readonly TimeSpan _retentionWindow;
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

    public OutboxRetentionPruner(
        IDbContextFactory<PercolatorDbContext> contextFactory,
        IEventPublisher eventPublisher,
        TimeProvider? timeProvider = null,
        TimeSpan? pruneInterval = null,
        TimeSpan? retentionWindow = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pruneInterval = pruneInterval ?? TimeSpan.FromHours(24);
        _retentionWindow = retentionWindow ?? TimeSpan.FromDays(7);
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        DateTimeOffset cutoffTime = _timeProvider.GetUtcNow() - _retentionWindow;
        int deliveredStatus = (int)OutboxStatus.Delivered;
        int totalPurged = 0;

        while (!ct.IsCancellationRequested)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var expiredIds = await context.OpsOutboxJobs
                .Where(j => j.Status == deliveredStatus && j.CreatedAtUtc <= cutoffTime)
                .OrderBy(j => j.CreatedAtUtc)
                .Select(j => j.Id)
                .Take(500)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (expiredIds.Count == 0)
            {
                break;
            }

            await context.OpsOutboxJobs
                .Where(j => expiredIds.Contains(j.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            totalPurged += expiredIds.Count;
            if (expiredIds.Count < 500)
            {
                break;
            }
        }

        if (totalPurged > 0)
        {
            await _eventPublisher.PublishAsync(
                new OutboxJobsPrunedEvent(Guid.NewGuid(), _timeProvider.GetUtcNow(), totalPurged),
                ct).ConfigureAwait(false);
        }

        return totalPurged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan currentRetryDelay = MinRetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
                currentRetryDelay = MinRetryDelay;
                await Task.Delay(_pruneInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(currentRetryDelay, _timeProvider, stoppingToken).ConfigureAwait(false);
                    currentRetryDelay = TimeSpan.FromTicks(Math.Min(currentRetryDelay.Ticks * 2, MaxRetryDelay.Ticks));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
