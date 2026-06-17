using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;

namespace Percolator.Infrastructure.Chat;

public sealed class DeliveryCertificateRefreshWorker : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DeliveryCertificateRefreshWorker> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public DeliveryCertificateRefreshWorker(
        IServiceProvider serviceProvider,
        ILogger<DeliveryCertificateRefreshWorker> logger,
        IHostApplicationLifetime lifetime)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(async () =>
        {
            await RunRefreshLoopAsync(cancellationToken);
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task RunRefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateAsyncScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<ICertificateOrchestrator>();

                _logger.LogInformation("Refreshing delivery certificate");
                await orchestrator.RefreshLocalCertificateAsync(ct);
                _logger.LogInformation("Delivery certificate refresh completed");

                // Wait 20 hours before next refresh (well before 24-hour expiration)
                await Task.Delay(TimeSpan.FromHours(20), ct);
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown - break the loop without logging as an error
                _logger.LogInformation("Delivery certificate refresh worker shutting down");
                break;
            }
            catch (CryptographicException ex)
            {
                // Permanent configuration error (e.g., corrupted key material) - do not retry
                _logger.LogCritical(ex, "Fatal cryptographic error during delivery certificate refresh. Worker will not retry.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during delivery certificate refresh");
                // Wait 5 minutes before retrying on transient errors
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
        }
    }
}
