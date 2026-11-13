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

public sealed class RelayPreKeyDirectoryPruner : BackgroundService
{
    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;
    private readonly IEventPublisher _eventPublisher;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pruneInterval;
    private readonly TimeSpan _retentionWindow;
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

    public RelayPreKeyDirectoryPruner(
        IDbContextFactory<PercolatorDbContext> contextFactory,
        IEventPublisher eventPublisher,
        TimeProvider timeProvider,
        TimeSpan? pruneInterval = null,
        TimeSpan? retentionWindow = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _pruneInterval = pruneInterval ?? TimeSpan.FromHours(24);
        _retentionWindow = retentionWindow ?? TimeSpan.FromDays(30);
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        DateTimeOffset cutoffTime = _timeProvider.GetUtcNow() - _retentionWindow;
        int totalPurged = 0;

        while (!ct.IsCancellationRequested)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var staleKeys = await context.RelayPreKeys
                .Where(k => k.UpdatedAtUtc <= cutoffTime)
                .OrderBy(k => k.UpdatedAtUtc)
                .Select(k => new { k.OwnerIdentityId, k.DeviceId })
                .Take(500)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (staleKeys.Count == 0)
            {
                break;
            }

            var stubs = staleKeys.Select(k => new RelayPreKeyEntity
            {
                OwnerIdentityId = k.OwnerIdentityId,
                DeviceId = k.DeviceId
            });
            context.RelayPreKeys.RemoveRange(stubs);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);

            totalPurged += staleKeys.Count;
            if (staleKeys.Count < 500)
            {
                break;
            }
        }

        if (totalPurged > 0)
        {
            await _eventPublisher.PublishAsync(
                new RelayPreKeysPrunedEvent(Guid.NewGuid(), _timeProvider.GetUtcNow(), totalPurged),
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
