// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Domain.Identities;
using R3;

namespace Percolator.Desktop.Features.Transports.Services;

/// <summary>
/// Stateful presentation service tracking active and pending peer connection state.
/// Holds the canonical list of staged outbound connections, orchestrates background retries,
/// and processes user cancellations.
/// </summary>
public sealed class PeerConnectionStateService : IPeerConnectionStateService
{
    private readonly IOutboundPendingRetryWorker _retryWorker;
    private readonly ILogger<PeerConnectionStateService> _logger;

    private readonly ObservableList<OutboundPendingConnectionModel> _pendingOutboundConnections = new();
    private readonly Subject<OutboundPendingConnectionModel> _connectionSucceeded = new();
    private readonly Subject<PublicIdentityId> _connectionCancelled = new();
    private readonly object _lock = new();

    private DisposableBag _bag;
    private bool _disposed;

    public PeerConnectionStateService(
        IOutboundPendingRetryWorker retryWorker,
        ILogger<PeerConnectionStateService> logger)
    {
        _retryWorker = retryWorker ?? throw new ArgumentNullException(nameof(retryWorker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _retryWorker.ConnectionSucceeded
            .Subscribe(model =>
            {
                lock (_lock)
                {
                    var existing = _pendingOutboundConnections.FirstOrDefault(p => p.PeerId == model.PeerId);
                    if (existing != null)
                    {
                        _logger.LogInformation("Removing peer {PeerId} from pending outbound connections upon successful connection", model.PeerId);
                        _pendingOutboundConnections.Remove(existing);
                    }
                }

                _connectionSucceeded.OnNext(model);
            })
            .AddTo(ref _bag);
    }

    public ObservableList<OutboundPendingConnectionModel> PendingOutboundConnections => _pendingOutboundConnections;

    public Observable<OutboundPendingConnectionModel> ConnectionSucceeded => _connectionSucceeded;

    public Observable<PublicIdentityId> ConnectionCancelled => _connectionCancelled;

    public void StagePendingOutbound(OutboundPendingConnectionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (_lock)
        {
            if (_pendingOutboundConnections.Any(p => p.PeerId == model.PeerId))
            {
                _logger.LogInformation("Peer {PeerId} is already staged as pending outbound; ignoring duplicate stage request", model.PeerId);
                return;
            }

            _logger.LogInformation("Staging peer {PeerId} ({DisplayName}) as pending outbound connection", model.PeerId, model.DisplayName);
            _pendingOutboundConnections.Add(model);
        }

        _retryWorker.EnqueueRetry(model);
    }

    public void CancelOutboundRequest(PublicIdentityId peerId)
    {
        var removed = false;
        lock (_lock)
        {
            var existing = _pendingOutboundConnections.FirstOrDefault(p => p.PeerId == peerId);
            if (existing != null)
            {
                _logger.LogInformation("Removing pending outbound connection request for peer {PeerId} on user cancellation", peerId);
                _pendingOutboundConnections.Remove(existing);
                removed = true;
            }
        }

        if (removed || _retryWorker.IsRetrying(peerId))
        {
            _retryWorker.CancelRetry(peerId);
            _connectionCancelled.OnNext(peerId);
        }
    }

    public bool IsPending(PublicIdentityId peerId)
    {
        lock (_lock)
        {
            return _pendingOutboundConnections.Any(p => p.PeerId == peerId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _bag.Dispose();

        _connectionSucceeded.OnCompleted();
        _connectionSucceeded.Dispose();

        _connectionCancelled.OnCompleted();
        _connectionCancelled.Dispose();
    }
}
