namespace Percolator.Infrastructure.Maintenance;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Percolator.Application2.Delivery.Events;
using Percolator.Application2.Ports;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class RelayProfileSlotPruner : BackgroundService
{
    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;
    private readonly IEventPublisher _eventPublisher;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pruneInterval;
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

    public RelayProfileSlotPruner(
        IDbContextFactory<PercolatorDbContext> contextFactory,
        IEventPublisher eventPublisher,
        TimeProvider timeProvider,
        TimeSpan? pruneInterval = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _pruneInterval = pruneInterval ?? TimeSpan.FromHours(1);
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        DateTimeOffset cutoffTime = _timeProvider.GetUtcNow();
        int totalPurged = 0;

        while (!ct.IsCancellationRequested)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var expiredLocators = await context.RelayProfileSlots
                .Where(s => s.ExpiresAtUtc <= cutoffTime)
                .OrderBy(s => s.ExpiresAtUtc)
                .Select(s => s.BlindedLocator)
                .Take(500)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (expiredLocators.Count == 0)
            {
                break;
            }

            var stubs = expiredLocators.Select(loc => new RelayProfileSlotEntity { BlindedLocator = loc });
            context.RelayProfileSlots.RemoveRange(stubs);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);

            totalPurged += expiredLocators.Count;
            if (expiredLocators.Count < 500)
            {
                break;
            }
        }

        if (totalPurged > 0)
        {
            await _eventPublisher.PublishAsync(
                new RelayProfileSlotsPrunedEvent(Guid.NewGuid(), _timeProvider.GetUtcNow(), totalPurged),
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
