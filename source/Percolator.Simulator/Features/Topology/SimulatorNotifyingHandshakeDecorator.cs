using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Topology;

internal sealed class SimulatorNotifyingHandshakeDecorator : IHandshakeService
{
    private readonly IHandshakeService _inner;
    private readonly ISimulatorStateBus _stateBus;
    private readonly SimulatedNodeId _nodeId;

    public SimulatorNotifyingHandshakeDecorator(
        IHandshakeService inner,
        ISimulatorStateBus stateBus,
        SimulatedNodeId nodeId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _nodeId = nodeId;
    }

    public async ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(
        PublicIdentityId ownerId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        PreKeyBundle remoteBundle,
        AppId? initialAppId = null,
        ReadOnlyMemory<byte>? initialPayload = null,
        CancellationToken ct = default)
    {
        var result = await _inner.InitiateHandshakeAsync(
            ownerId,
            ownerDeviceId,
            remotePeerId,
            remoteBundle,
            initialAppId,
            initialPayload,
            ct);

        if (result.IsSuccess)
        {
            _stateBus.Publish(new SimulatorStateSignal(
                _nodeId,
                SignalCategory.RatchetSessionChanged,
                Details: remotePeerId.ToString(),
                Payload: new HandshakeSignalPayload(remotePeerId, "Initiate")));
        }

        return result;
    }

    public async ValueTask<DomainResult> ReceiveInvitationAsync(
        InboundHandshakeEnvelope envelope,
        CancellationToken ct = default)
    {
        var result = await _inner.ReceiveInvitationAsync(envelope, ct);

        if (result.IsSuccess)
        {
            _stateBus.Publish(new SimulatorStateSignal(
                _nodeId,
                SignalCategory.PendingHandshakeChanged,
                Details: envelope.SenderIdentityId.ToString(),
                Payload: new HandshakeSignalPayload(envelope.SenderIdentityId, "ReceiveInvitation")));

            _stateBus.Publish(new SimulatorStateSignal(
                _nodeId,
                SignalCategory.RatchetSessionChanged,
                Details: envelope.SenderIdentityId.ToString(),
                Payload: new HandshakeSignalPayload(envelope.SenderIdentityId, "ReceiveInvitation")));
        }

        return result;
    }

    public async ValueTask<DomainResult> CompletePendingHandshakeAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        CancellationToken ct = default)
    {
        var result = await _inner.CompletePendingHandshakeAsync(recipientId, senderId, ct);

        if (result.IsSuccess)
        {
            _stateBus.Publish(new SimulatorStateSignal(
                _nodeId,
                SignalCategory.PendingHandshakeChanged,
                Details: senderId.ToString(),
                Payload: new HandshakeSignalPayload(senderId, "CompletePending")));

            _stateBus.Publish(new SimulatorStateSignal(
                _nodeId,
                SignalCategory.RatchetSessionChanged,
                Details: senderId.ToString(),
                Payload: new HandshakeSignalPayload(senderId, "CompletePending")));
        }

        return result;
    }
}
