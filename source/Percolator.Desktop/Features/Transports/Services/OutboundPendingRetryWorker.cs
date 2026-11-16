// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/R3.spec.md before modifying this file.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Domain.Identities;
using Percolator.PluginSdk;
using R3;

namespace Percolator.Desktop.Features.Transports.Services;

/// <summary>
/// Hosted service managing background direct connection retry loops with exponential backoff.
/// Runs indefinitely while the persona session is active until the remote peer is reachable
/// or the request is cancelled by the user.
/// </summary>
public sealed class OutboundPendingRetryWorker : IOutboundPendingRetryWorker
{
    public static readonly TimeSpan[] BackoffSchedule = new[]
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    };

    private readonly IPeerConnectionService _peerConnectionService;
    private readonly ILogger<OutboundPendingRetryWorker> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly Subject<OutboundPendingConnectionModel> _connectionSucceeded = new();
    private readonly ConcurrentDictionary<PublicIdentityId, (CancellationTokenSource Cts, Task WorkerTask)> _activeRetries = new();
    private readonly CancellationTokenSource _stoppingCts = new();
    private bool _disposed;

    public OutboundPendingRetryWorker(
        IPeerConnectionService peerConnectionService,
        ILogger<OutboundPendingRetryWorker> logger,
        TimeProvider timeProvider)
    {
        _peerConnectionService = peerConnectionService ?? throw new ArgumentNullException(nameof(peerConnectionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Observable<OutboundPendingConnectionModel> ConnectionSucceeded => _connectionSucceeded;

    public IReadOnlyCollection<PublicIdentityId> ActiveRetryPeerIds => _activeRetries.Keys.ToList();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Outbound pending retry worker started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping outbound pending retry worker; cancelling all active retry loops");
        _stoppingCts.Cancel();
        return Task.CompletedTask;
    }

    public void EnqueueRetry(OutboundPendingConnectionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (_disposed || _stoppingCts.IsCancellationRequested)
        {
            _logger.LogWarning("Cannot enqueue retry for peer {PeerId}; retry worker is stopped or disposed", model.PeerId);
            return;
        }

        if (_activeRetries.ContainsKey(model.PeerId))
        {
            _logger.LogDebug("Retry loop for peer {PeerId} is already active", model.PeerId);
            return;
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingCts.Token);
        var task = Task.Run(() => ExecuteRetryLoopAsync(model, linkedCts));

        if (!_activeRetries.TryAdd(model.PeerId, (linkedCts, task)))
        {
            linkedCts.Cancel();
            linkedCts.Dispose();
        }
    }

    public void CancelRetry(PublicIdentityId peerId)
    {
        if (_activeRetries.TryRemove(peerId, out var entry))
        {
            _logger.LogInformation("Cancelling retry loop for peer {PeerId}", peerId);
            try
            {
                entry.Cts.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception while signalling cancellation for peer {PeerId}", peerId);
            }
        }
    }

    public bool IsRetrying(PublicIdentityId peerId)
    {
        return _activeRetries.ContainsKey(peerId);
    }

    public static TimeSpan GetBackoffDelay(int attempt)
    {
        if (attempt < 0) return BackoffSchedule[0];
        if (attempt >= BackoffSchedule.Length) return BackoffSchedule[^1];
        return BackoffSchedule[attempt];
    }

    private async Task ExecuteRetryLoopAsync(OutboundPendingConnectionModel model, CancellationTokenSource linkedCts)
    {
        _logger.LogInformation("Starting background retry loop for direct peer {PeerId} ({DisplayName})",
            model.PeerId, model.DisplayName);

        var ct = linkedCts.Token;
        var attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var delay = GetBackoffDelay(attempt);
                _logger.LogDebug("Awaiting backoff delay {Delay} (attempt {Attempt}) for peer {PeerId}",
                    delay, attempt + 1, model.PeerId);

                try
                {
                    await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Retry loop cancelled during delay for peer {PeerId}", model.PeerId);
                    break;
                }

                if (ct.IsCancellationRequested) break;

                Uri directUri;
                try
                {
                    var rawHost = model.Invitation.EndpointHosts.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)) ?? "127.0.0.1";
                    var port = model.Invitation.PortRange?.MinPort ?? 5100;

                    if (rawHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        rawHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    {
                        var parsed = new Uri(rawHost);
                        directUri = new UriBuilder(parsed) { Scheme = "https", Port = port }.Uri;
                    }
                    else
                    {
                        directUri = new UriBuilder("https", rawHost, port).Uri;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve direct endpoint URI for pending peer {PeerId}", model.PeerId);
                    attempt++;
                    continue;
                }

                // 3-second fast probe timeout per retry attempt
                using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3), _timeProvider);
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, probeCts.Token);

                try
                {
                    var result = await _peerConnectionService.ConnectToDirectPeerAsync(
                        model.Invitation.IdentityId,
                        model.Invitation.RoutingToken,
                        model.Invitation.DeliveryToken,
                        directUri,
                        ct: attemptCts.Token).ConfigureAwait(false);

                    if (result.IsSuccess)
                    {
                        _logger.LogInformation("Outbound direct connection to peer {PeerId} succeeded on retry attempt {Attempt}",
                            model.PeerId, attempt + 1);

                        _connectionSucceeded.OnNext(model);
                        break;
                    }
                    else
                    {
                        _logger.LogDebug("Retry attempt {Attempt} for peer {PeerId} failed: {Error}",
                            attempt + 1, model.PeerId, result.Error.Description);
                    }
                }
                catch (OperationCanceledException) when (probeCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    _logger.LogDebug("Probe timed out after 3s on retry attempt {Attempt} for peer {PeerId}", attempt + 1, model.PeerId);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Retry attempt cancelled for peer {PeerId}", model.PeerId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unexpected exception during retry attempt {Attempt} for peer {PeerId}", attempt + 1, model.PeerId);
                }

                attempt++;
            }
        }
        finally
        {
            _activeRetries.TryRemove(model.PeerId, out _);
            linkedCts.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stoppingCts.Cancel();
        _stoppingCts.Dispose();

        _activeRetries.Clear();

        _connectionSucceeded.OnCompleted();
        _connectionSucceeded.Dispose();
    }
}
