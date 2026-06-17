using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;

namespace Percolator.Infrastructure.Chat;

public sealed class DeliveryCertificateRefreshWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeliveryCertificateRefreshWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public DeliveryCertificateRefreshWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<DeliveryCertificateRefreshWorker> logger,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateAsyncScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<ICertificateOrchestrator>();

                _logger.LogInformation("Refreshing delivery certificate");
                await orchestrator.RefreshLocalCertificateAsync(stoppingToken);
                _logger.LogInformation("Delivery certificate refresh completed");

                // Wait 20 hours before next refresh (well before 24-hour expiration)
                await Task.Delay(TimeSpan.FromHours(20), _timeProvider, stoppingToken);
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
                await Task.Delay(TimeSpan.FromMinutes(5), _timeProvider, stoppingToken);
            }
        }
    }
}
