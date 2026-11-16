using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Chaos;

internal sealed class ChaosRelayMailboxCapabilityStoreDecorator : IRelayMailboxCapabilityStore
{
    private readonly IRelayMailboxCapabilityStore _inner;
    private readonly ISimulatorChaosController _chaosController;
    private readonly SimulatedNodeId _relayNodeId;

    public ChaosRelayMailboxCapabilityStoreDecorator(
        IRelayMailboxCapabilityStore inner,
        ISimulatorChaosController chaosController,
        SimulatedNodeId relayNodeId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _chaosController = chaosController ?? throw new ArgumentNullException(nameof(chaosController));
        _relayNodeId = relayNodeId;
    }

    public ValueTask RegisterAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        DeliveryToken drainToken,
        CancellationToken ct = default)
    {
        return _inner.RegisterAsync(routingToken, deliveryToken, drainToken, ct);
    }

    public ValueTask<DomainResult> RegisterVerifiedAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        DeliveryToken drainToken,
        DeliveryToken? existingDrainToken = null,
        CancellationToken ct = default)
    {
        return _inner.RegisterVerifiedAsync(routingToken, deliveryToken, drainToken, existingDrainToken, ct);
    }

    public ValueTask<bool> VerifyDeliveryTokenAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        CancellationToken ct = default)
    {
        return _inner.VerifyDeliveryTokenAsync(routingToken, deliveryToken, ct);
    }

    public ValueTask<bool> VerifyDrainTokenAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken drainToken,
        CancellationToken ct = default)
    {
        return _inner.VerifyDrainTokenAsync(routingToken, drainToken, ct);
    }

    public ValueTask<DeliveryToken?> GetDrainTokenAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default)
    {
        return _inner.GetDrainTokenAsync(routingToken, ct);
    }

    public async ValueTask EnqueueEnvelopeAsync(
        BlindedRoutingToken routingToken,
        RelayQueuedEnvelopeRecord envelope,
        CancellationToken ct = default)
    {
        var action = _chaosController.EvaluateDeliveryFault(_relayNodeId);
        switch (action)
        {
            case ChaosFaultAction.DropEnvelope:
                return;

            case ChaosFaultAction.SimulateTransientError:
                throw new InvalidOperationException("Injected transient relay failure.");

            case ChaosFaultAction.InjectArtificialDelay:
                await Task.Delay(500, ct).ConfigureAwait(false);
                await _inner.EnqueueEnvelopeAsync(routingToken, envelope, ct).ConfigureAwait(false);
                break;

            case ChaosFaultAction.Pass:
            default:
                await _inner.EnqueueEnvelopeAsync(routingToken, envelope, ct).ConfigureAwait(false);
                break;
        }
    }

    public ValueTask<IReadOnlyList<RelayQueuedEnvelopeRecord>> GetEnvelopesAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default)
    {
        return _inner.GetEnvelopesAsync(routingToken, ct);
    }

    public ValueTask<int> PurgeMailboxAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default)
    {
        return _inner.PurgeMailboxAsync(routingToken, ct);
    }

    public IDisposable SubscribeMailbox(
        BlindedRoutingToken routingToken,
        ChannelWriter<RelayQueuedEnvelopeRecord> writer)
    {
        return _inner.SubscribeMailbox(routingToken, writer);
    }

    public ValueTask<bool> AcknowledgeEnvelopeAsync(
        BlindedRoutingToken routingToken,
        MailboxQueueId queueId,
        CancellationToken ct = default)
    {
        return _inner.AcknowledgeEnvelopeAsync(routingToken, queueId, ct);
    }
}
