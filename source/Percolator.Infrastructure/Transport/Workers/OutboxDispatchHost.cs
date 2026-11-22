namespace Percolator.Infrastructure.Transport.Workers;

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Events;
using Percolator.PluginSdk.Events;

public sealed class OutboxDispatchHostOptions
{
    public int BatchSize { get; init; } = 50;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed class OutboxDispatchHost : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventSubscriber _eventSubscriber;
    private readonly OutboxDispatchHostOptions _options;
    private readonly ILogger<OutboxDispatchHost> _logger;
    private readonly Channel<bool> _signalChannel;
    private IDisposable? _subscription;

    public OutboxDispatchHost(
        IServiceScopeFactory scopeFactory,
        IEventSubscriber eventSubscriber,
        OutboxDispatchHostOptions options,
        ILogger<OutboxDispatchHost> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _eventSubscriber = eventSubscriber ?? throw new ArgumentNullException(nameof(eventSubscriber));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _signalChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _eventSubscriber.Subscribe<OutboxJobEnqueuedEvent>((_, _) =>
        {
            _signalChannel.Writer.TryWrite(true);
            return ValueTask.CompletedTask;
        });

        // Trigger initial check on startup
        _signalChannel.Writer.TryWrite(true);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(_options.PollInterval);

                try
                {
                    await _signalChannel.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
                    while (_signalChannel.Reader.TryRead(out _)) { }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Timed poll interval expired; proceed to check due pending jobs
                }

                if (stoppingToken.IsCancellationRequested) break;

                int processed;
                do
                {
                    using var scope = _scopeFactory.CreateScope();
                    var worker = scope.ServiceProvider.GetRequiredService<OutboxWorker>();
                    processed = await worker.ProcessBatchAsync(_options.BatchSize, stoppingToken).ConfigureAwait(false);
                }
                while (processed > 0 && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error processing outbox jobs in OutboxDispatchHost.");
                try
                {
                    await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
