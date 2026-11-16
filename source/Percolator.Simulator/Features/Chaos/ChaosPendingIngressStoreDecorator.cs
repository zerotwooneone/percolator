using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Chaos;

internal sealed class ChaosPendingIngressStoreDecorator : IPendingIngressStore
{
    private readonly IPendingIngressStore _inner;
    private readonly ISimulatorChaosController _chaosController;
    private readonly SimulatedNodeId _nodeId;

    public ChaosPendingIngressStoreDecorator(
        IPendingIngressStore inner,
        ISimulatorChaosController chaosController,
        SimulatedNodeId nodeId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _chaosController = chaosController ?? throw new ArgumentNullException(nameof(chaosController));
        _nodeId = nodeId;
    }

    public async Task<PendingIngressRecord> UpsertAsync(
        PublicIdentityId ownerId,
        QueuedMailboxEnvelope envelope,
        CancellationToken ct = default)
    {
        var action = _chaosController.EvaluateDeliveryFault(_nodeId);
        switch (action)
        {
            case ChaosFaultAction.DropEnvelope:
                // Return synthesized in-memory record to satisfy caller without persisting to inner durable store
                return new PendingIngressRecord(envelope.QueueId, envelope, 0, null);

            case ChaosFaultAction.SimulateTransientError:
                throw new InvalidOperationException("Injected transient ingress failure.");

            case ChaosFaultAction.InjectArtificialDelay:
                await Task.Delay(500, ct).ConfigureAwait(false);
                return await _inner.UpsertAsync(ownerId, envelope, ct).ConfigureAwait(false);

            case ChaosFaultAction.Pass:
            default:
                return await _inner.UpsertAsync(ownerId, envelope, ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<PendingIngressRecord>> ListByOwnerAsync(
        PublicIdentityId ownerId,
        CancellationToken ct = default)
    {
        var action = _chaosController.EvaluateDeliveryFault(_nodeId);
        switch (action)
        {
            case ChaosFaultAction.SimulateTransientError:
                throw new InvalidOperationException("Injected transient ingress failure.");

            case ChaosFaultAction.InjectArtificialDelay:
                await Task.Delay(500, ct).ConfigureAwait(false);
                return await _inner.ListByOwnerAsync(ownerId, ct).ConfigureAwait(false);

            case ChaosFaultAction.Pass:
            case ChaosFaultAction.DropEnvelope:
            default:
                return await _inner.ListByOwnerAsync(ownerId, ct).ConfigureAwait(false);
        }
    }

    public Task DeleteAsync(
        PublicIdentityId ownerId,
        MailboxQueueId queueId,
        CancellationToken ct = default)
    {
        return _inner.DeleteAsync(ownerId, queueId, ct);
    }

    public Task RecordTransientFailureAsync(
        PublicIdentityId ownerId,
        MailboxQueueId queueId,
        DomainError error,
        CancellationToken ct = default)
    {
        return _inner.RecordTransientFailureAsync(ownerId, queueId, error, ct);
    }
}
