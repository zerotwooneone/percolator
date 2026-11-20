namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Apps.FileTransfer.Ports;

public sealed class TransferOfferReaperWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _sweepInterval;
    private readonly TimeSpan _retentionWindow;

    public TransferOfferReaperWorker(
        IServiceScopeFactory scopeFactory,
        TimeSpan? sweepInterval = null,
        TimeSpan? retentionWindow = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _sweepInterval = sweepInterval ?? TimeSpan.FromMinutes(15);
        _retentionWindow = retentionWindow ?? TimeSpan.FromHours(24);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_sweepInterval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                ITransferOfferReaperService reaper = scope.ServiceProvider.GetRequiredService<ITransferOfferReaperService>();
                await reaper.SweepAndExpireStaleOffersAsync(_retentionWindow, stoppingToken).ConfigureAwait(false);
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
