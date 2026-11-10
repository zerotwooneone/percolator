using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed class SimulatorNotifyingRelayCapabilityStoreDecorator : IRelayMailboxCapabilityStore, IRelayQueueChaosController
{
    private readonly ISimulatorStateBus _stateBus;
    private readonly SimulatedNodeId _nodeId;
    private readonly InMemoryRelayMailboxCapabilityStore _inner;

    public SimulatorNotifyingRelayCapabilityStoreDecorator(
        ISimulatorStateBus stateBus,
        SimulatedNodeId nodeId)
        : this(stateBus, nodeId, new InMemoryRelayMailboxCapabilityStore())
    {
    }

    public SimulatorNotifyingRelayCapabilityStoreDecorator(
        ISimulatorStateBus stateBus,
        SimulatedNodeId nodeId,
        InMemoryRelayMailboxCapabilityStore inner)
    {
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _nodeId = nodeId;
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public int TotalEnvelopeCount => _inner.TotalEnvelopeCount;

    public IReadOnlyList<QueuedSealedEnvelopeProto> GetHeldEnvelopes() => _inner.GetHeldEnvelopes();

    public async ValueTask RegisterAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        PublicIdentityId? ownerId = null,
        DeviceId? deviceId = null,
        CancellationToken ct = default)
    {
        await _inner.RegisterAsync(routingToken, deliveryToken, drainToken, ownerId, deviceId, ct).ConfigureAwait(false);
        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "MailboxRegistered"));
    }

    public async ValueTask<DomainResult> RegisterVerifiedAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        ReadOnlyMemory<byte> existingDrainToken = default,
        PublicIdentityId? ownerId = null,
        DeviceId? deviceId = null,
        CancellationToken ct = default)
    {
        var result = await _inner.RegisterVerifiedAsync(
            routingToken, deliveryToken, drainToken, existingDrainToken, ownerId, deviceId, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "MailboxRegistered"));
        }

        return result;
    }

    public async ValueTask AssociateOwnerAsync(
        ReadOnlyMemory<byte> routingToken,
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken ct = default)
    {
        await _inner.AssociateOwnerAsync(routingToken, ownerId, deviceId, ct).ConfigureAwait(false);
        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "OwnerAssociated"));
    }

    public ValueTask<(PublicIdentityId? OwnerId, DeviceId? DeviceId)> GetOwnerAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        return _inner.GetOwnerAsync(routingToken, ct);
    }

    public ValueTask<(byte[] RoutingToken, byte[] DeliveryToken)?> GetMailboxTokensByOwnerAsync(
        PublicIdentityId ownerId,
        CancellationToken ct = default)
    {
        return _inner.GetMailboxTokensByOwnerAsync(ownerId, ct);
    }

    public ValueTask<bool> VerifyDeliveryTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        CancellationToken ct = default)
    {
        return _inner.VerifyDeliveryTokenAsync(routingToken, deliveryToken, ct);
    }

    public ValueTask<bool> VerifyDrainTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> drainToken,
        CancellationToken ct = default)
    {
        return _inner.VerifyDrainTokenAsync(routingToken, drainToken, ct);
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetDrainTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        return _inner.GetDrainTokenAsync(routingToken, ct);
    }

    public async ValueTask EnqueueEnvelopeAsync(
        ReadOnlyMemory<byte> routingToken,
        QueuedSealedEnvelopeProto envelope,
        CancellationToken ct = default)
    {
        await _inner.EnqueueEnvelopeAsync(routingToken, envelope, ct).ConfigureAwait(false);
        _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "EnvelopeEnqueued"));
    }

    public ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> GetEnvelopesAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        return _inner.GetEnvelopesAsync(routingToken, ct);
    }

    public async ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> DrainEnvelopesAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        var drained = await _inner.DrainEnvelopesAsync(routingToken, ct).ConfigureAwait(false);
        if (drained.Count > 0)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "EnvelopesDrained"));
        }
        return drained;
    }

    public IDisposable SubscribeMailbox(
        ReadOnlyMemory<byte> routingToken,
        ChannelWriter<QueuedSealedEnvelopeProto> writer)
    {
        return _inner.SubscribeMailbox(routingToken, writer);
    }

    public async ValueTask<bool> AcknowledgeEnvelopeAsync(
        ReadOnlyMemory<byte> routingToken,
        string queueId,
        CancellationToken ct = default)
    {
        bool acknowledged = await _inner.AcknowledgeEnvelopeAsync(routingToken, queueId, ct).ConfigureAwait(false);
        if (acknowledged)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "EnvelopeAcknowledged"));
        }
        return acknowledged;
    }

    public bool DuplicateEnvelope(string? queueId = null)
    {
        bool result = _inner.DuplicateEnvelope(queueId);
        if (result)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "ChaosDuplicate"));
        }
        return result;
    }

    public bool FlipBit(string? queueId = null, int byteOffset = 0)
    {
        bool result = _inner.FlipBit(queueId, byteOffset);
        if (result)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "ChaosFlipBit"));
        }
        return result;
    }

    public bool ShuffleQueue()
    {
        bool result = _inner.ShuffleQueue();
        if (result)
        {
            _stateBus.Publish(new SimulatorStateSignal(_nodeId, SignalCategory.RelayQueueChanged, "ChaosShuffle"));
        }
        return result;
    }
}
