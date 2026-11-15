namespace Percolator.Infrastructure.Transport.Workers;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Percolator.Application.Delivery;

/// <summary>
/// Hosted entry point for the mailbox drain pipeline: delegates the process
/// lifetime to <see cref="DrainScheduler.RunAsync"/>, which reconciles active
/// personas against relay mailbox bindings and supervises one stream per target.
/// </summary>
public sealed class RelayMailboxDrainHost : BackgroundService
{
    private readonly DrainScheduler _scheduler;

    public RelayMailboxDrainHost(DrainScheduler scheduler)
    {
        _scheduler = scheduler ?? throw new System.ArgumentNullException(nameof(scheduler));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => _scheduler.RunAsync(stoppingToken);
}
