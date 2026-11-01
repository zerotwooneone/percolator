namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Infrastructure.Persistence;

public sealed class SwarmEndpointRetentionPruner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _pruneInterval;
    private readonly TimeSpan _retentionWindow;

    public SwarmEndpointRetentionPruner(
        IServiceScopeFactory scopeFactory,
        TimeSpan? pruneInterval = null,
        TimeSpan? retentionWindow = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _pruneInterval = pruneInterval ?? TimeSpan.FromMinutes(30);
        _retentionWindow = retentionWindow ?? TimeSpan.FromHours(24);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_pruneInterval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                PercolatorDbContext dbContext = scope.ServiceProvider.GetRequiredService<PercolatorDbContext>();

                DateTimeOffset cutoff = DateTimeOffset.UtcNow - _retentionWindow;
                var staleRows = await dbContext.SwarmEndpoints
                    .Where(e => e.LastSeenUtc < cutoff)
                    .ToListAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (staleRows.Count > 0)
                {
                    dbContext.SwarmEndpoints.RemoveRange(staleRows);
                    await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Fault-tolerant background worker
            }
        }
    }
}
