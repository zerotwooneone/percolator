namespace Percolator.Infrastructure.Transport.Workers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

public sealed class RelayGroupStreamWorkerOptions
{
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(50);
}

public sealed class RelayGroupStreamWorker : BackgroundService
{
    private readonly Func<CancellationToken, Task> _subscriptionRunner;
    private readonly RelayGroupStreamWorkerOptions _options;

    public RelayGroupStreamWorker(
        Func<CancellationToken, Task> subscriptionRunner,
        RelayGroupStreamWorkerOptions options)
    {
        _subscriptionRunner = subscriptionRunner ?? throw new ArgumentNullException(nameof(subscriptionRunner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _subscriptionRunner(stoppingToken);
                // Pause briefly after completed runner cycle to prevent tight CPU spin
                await Task.Delay(_options.InitialBackoff, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(_options.InitialBackoff, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
