namespace Percolator.Infrastructure.Transport.Workers;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Infrastructure.Persistence;

public sealed class RelayDiscoveryRegistrationPruner : BackgroundService
{
    public static readonly TimeSpan DefaultRegistrationTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultEntryTtl = TimeSpan.FromHours(48);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly PercolatorDbContext? _directDbContext;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _registrationTtl;
    private readonly TimeSpan _entryTtl;
    private readonly TimeSpan _interval;

    public RelayDiscoveryRegistrationPruner(
        IServiceScopeFactory scopeFactory,
        TimeProvider? timeProvider = null,
        TimeSpan? registrationTtl = null,
        TimeSpan? entryTtl = null,
        TimeSpan? interval = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _registrationTtl = registrationTtl ?? DefaultRegistrationTtl;
        _entryTtl = entryTtl ?? DefaultEntryTtl;
        _interval = interval ?? DefaultInterval;
    }

    public RelayDiscoveryRegistrationPruner(
        PercolatorDbContext dbContext,
        TimeProvider? timeProvider = null,
        TimeSpan? registrationTtl = null,
        TimeSpan? entryTtl = null,
        TimeSpan? interval = null)
    {
        _directDbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _registrationTtl = registrationTtl ?? DefaultRegistrationTtl;
        _entryTtl = entryTtl ?? DefaultEntryTtl;
        _interval = interval ?? DefaultInterval;
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        if (_directDbContext != null)
        {
            return await ExecuteSweepOnDbContextAsync(_directDbContext, ct);
        }

        using var scope = _scopeFactory!.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PercolatorDbContext>();
        return await ExecuteSweepOnDbContextAsync(dbContext, ct);
    }

    private async Task<int> ExecuteSweepOnDbContextAsync(
        PercolatorDbContext dbContext,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var directoryCutoff = nowUtc.Subtract(_registrationTtl);
        var rendezvousCutoff = nowUtc.Subtract(_entryTtl);

        var staleDirectoryEntries = await dbContext.RelayPeerDirectory
            .Where(e => e.LastUpdatedAtUtc <= directoryCutoff)
            .ToListAsync(ct);

        var staleRendezvousEntries = await dbContext.RelayRendezvousEntries
            .Where(e => e.PublishedAtUtc <= rendezvousCutoff)
            .ToListAsync(ct);

        int totalPruned = staleDirectoryEntries.Count + staleRendezvousEntries.Count;
        if (totalPruned == 0)
        {
            return 0;
        }

        if (staleDirectoryEntries.Count > 0)
        {
            dbContext.RelayPeerDirectory.RemoveRange(staleDirectoryEntries);
        }

        if (staleRendezvousEntries.Count > 0)
        {
            dbContext.RelayRendezvousEntries.RemoveRange(staleRendezvousEntries);
        }

        await dbContext.SaveChangesAsync(ct);
        return totalPruned;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _timeProvider);
        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }
}
