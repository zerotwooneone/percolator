namespace Percolator.Desktop.Tests.Features.Transports;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Domain.Identities;
using R3;

internal sealed class FakeOutboundPendingRetryWorker : IOutboundPendingRetryWorker
{
    private readonly Subject<OutboundPendingConnectionModel> _connectionSucceeded = new();
    public List<OutboundPendingConnectionModel> EnqueuedRetries { get; } = new();
    public List<PublicIdentityId> CancelledRetries { get; } = new();

    public Observable<OutboundPendingConnectionModel> ConnectionSucceeded => _connectionSucceeded;

    public IReadOnlyCollection<PublicIdentityId> ActiveRetryPeerIds =>
        EnqueuedRetries.Select(r => r.PeerId).Except(CancelledRetries).ToList();

    public void EnqueueRetry(OutboundPendingConnectionModel model)
    {
        EnqueuedRetries.Add(model);
    }

    public void CancelRetry(PublicIdentityId peerId)
    {
        CancelledRetries.Add(peerId);
    }

    public bool IsRetrying(PublicIdentityId peerId)
    {
        return EnqueuedRetries.Any(r => r.PeerId == peerId) && !CancelledRetries.Contains(peerId);
    }

    public void SimulateSuccess(OutboundPendingConnectionModel model)
    {
        _connectionSucceeded.OnNext(model);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _connectionSucceeded.OnCompleted();
        _connectionSucceeded.Dispose();
    }
}
