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
                var queries = scope.ServiceProvider.GetRequiredService<IDeliveryCertificateQueries>();
                var store = scope.ServiceProvider.GetRequiredService<IDeliveryCertificateStore>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<ICertificateOrchestrator>();

                // Query all active relay assignments
                var assignments = await queries.GetActiveRelayAssignmentsAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Found {Count} active relay assignments to check", assignments.Count);

                foreach (var (selfId, relayPeerId) in assignments)
                {
                    try
                    {
                        // Load existing certificate
                        var cert = await store.GetCertificateAsync(selfId, relayPeerId, stoppingToken).ConfigureAwait(false);
                        
                        // Check if certificate is missing or expires within 4 hours
                        var needsRefresh = cert == null || cert.ExpiresAtUtc < _timeProvider.GetUtcNow() + TimeSpan.FromHours(4);
                        
                        if (needsRefresh)
                        {
                            _logger.LogInformation("Refreshing delivery certificate for SelfId {SelfId}, RelayPeerId {RelayPeerId}", selfId, relayPeerId);
                            await orchestrator.RefreshLocalCertificateAsync(selfId, relayPeerId, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log per-relay failure but continue with other relays
                        _logger.LogError(ex, "Failed to refresh delivery certificate for SelfId {SelfId}, RelayPeerId {RelayPeerId}", selfId, relayPeerId);
                    }
                }

                // Wait 1 hour before next refresh cycle
                await Task.Delay(TimeSpan.FromHours(1), _timeProvider, stoppingToken);
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
                _logger.LogError(ex, "Error during delivery certificate refresh cycle");
                // Wait 5 minutes before retrying on transient errors
                await Task.Delay(TimeSpan.FromMinutes(5), _timeProvider, stoppingToken);
            }
        }
    }
}
