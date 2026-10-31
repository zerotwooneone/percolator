namespace Percolator.Infrastructure.Transport.Workers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

public sealed class RelayMailboxStreamWorkerOptions
{
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(5);
    public double BackoffMultiplier { get; set; } = 2.0;
}

public sealed class RelayMailboxStreamWorker : BackgroundService
{
    private readonly Func<CancellationToken, Task> _streamSessionRunner;
    private readonly RelayMailboxStreamWorkerOptions _options;
    public int RetryCount { get; private set; }
    public TimeSpan LastBackoffDelay { get; private set; }

    public RelayMailboxStreamWorker(
        Func<CancellationToken, Task> streamSessionRunner,
        RelayMailboxStreamWorkerOptions? options = null)
    {
        _streamSessionRunner = streamSessionRunner ?? throw new ArgumentNullException(nameof(streamSessionRunner));
        _options = options ?? new RelayMailboxStreamWorkerOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var currentBackoff = _options.InitialBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _streamSessionRunner(stoppingToken);
                // Reset backoff on successful completion
                currentBackoff = _options.InitialBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                RetryCount++;
                LastBackoffDelay = currentBackoff;

                try
                {
                    await Task.Delay(currentBackoff, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                // Exponential backoff
                var nextBackoffTicks = (long)(currentBackoff.Ticks * _options.BackoffMultiplier);
                currentBackoff = TimeSpan.FromTicks(Math.Min(nextBackoffTicks, _options.MaxBackoff.Ticks));
            }
        }
    }
}
