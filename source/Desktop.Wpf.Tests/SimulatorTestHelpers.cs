using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Models;
using ObservableCollections;
using Percolator.Identity;
using PeerId = Percolator.Network.PeerId;

namespace Desktop.Wpf.Tests;

public sealed class StateStub : TestSimulatorStateServiceBase
{
}

public abstract class TestSimulatorStateServiceBase : ISimulatorStateService
{
    private readonly ObservableList<SimulatedPeerModel> _peers = new();
    private readonly ObservableList<SimulatedRelayModel> _relays = new();
    private readonly ObservableList<PeerRelationship> _relationships = new();

    public IReadOnlyObservableList<SimulatedPeerModel> Peers => _peers;
    public IReadOnlyObservableList<SimulatedRelayModel> Relays => _relays;
    public IReadOnlyObservableList<PeerRelationship> Relationships => _relationships;

    public PeerId? ResolvePkhToPeerId { get; set; }

    public virtual void AddRelay(PeerId relayHostPeerId, bool autoDeliverEnabled)
    {
        var relay = new SimulatedRelayModel(relayHostPeerId);
        relay.AutoDeliverEnabled.Value = autoDeliverEnabled;
        _relays.Add(relay);
    }

    public virtual void AddRelay(SimulatedRelayModel relay) => _relays.Add(relay);

    public virtual Task<PeerId?> TryGetPeerIdByIdentityPublicKeyHashAsync(IdentityPublicKeyHash recipientPublicKeyHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ResolvePkhToPeerId);
    }

    public virtual Task<bool> DeleteRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relay = _relays.FirstOrDefault(r => r.RelayHostPeerId.Value == relayHostPeerId.Value);
        if (relay is null) return Task.FromResult(false);
        var removed = relay.RemoveMessage(ackId);
        return Task.FromResult(removed);
    }

    public virtual Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(PeerId relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, Guid ackId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<PeerId> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task RemovePeerAsync(PeerId peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task EnqueueRelayUpstreamToMainAsync(PeerId relayHostPeerId, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task EnqueueRelayDownstreamToPeerAsync(PeerId relayHostPeerId, Percolator.Identity.IdentityPublicKeyHash targetIdentityPublicKeyHash, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(PeerId relayHostPeerId, byte[] targetIdentityPublicKeyHash, int max, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<int> ForwardRelayUpstreamToMainAsync(PeerId relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, int max, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<bool> MoveRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<bool> CorruptRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task AddPublishedKeysRelationshipAsync(PeerId publisherPeerId, PeerId hostPeerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task RemovePublishedKeysRelationshipAsync(PeerId publisherPeerId, PeerId hostPeerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task AddRelayActiveSessionAsync(PeerId relayHostPeerId, PeerId peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task RemoveRelayActiveSessionAsync(PeerId relayHostPeerId, PeerId peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Contracts.EstablishDirectSessionResponse> ReceiveEstablishDirectSessionFromMainAsync(PeerId simulatedPeerId, PeerId mainPeerId, Percolator.Contracts.EstablishDirectSessionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<SimulatedPeerInviteAcceptance> AcceptReverseSignalInviteAsync(PeerId simulatedPeerId, PeerId inviterPeerId, Percolator.Contracts.EstablishDirectSessionRequest invite, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeliverInviteHandshakeResponseToMainAsync(Percolator.Contracts.InviteHandshakeResponse response, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ReceiveInviteHandshakeResponseFromMainAsync(PeerId simulatedPeerId, Percolator.Contracts.InviteHandshakeResponse response, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task QueueInviteHandshakeResponseForDeliveryToMainAsync(PeerId simulatedPeerId, Guid requestCorrelationId, Percolator.Contracts.InviteHandshakeResponse response, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<bool> TryDeliverQueuedInviteHandshakeResponseToMainAsync(PeerId simulatedPeerId, Guid requestCorrelationId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Cryptography.SessionId?> TryFinalizeInviteHandshakeResponseFromMainAsync(PeerId simulatedPeerId, PeerId acceptorPeerId, Guid requestCorrelationId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Contracts.EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(PeerId simulatedPeerId, Percolator.Contracts.EstablishSessionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Contracts.DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(PeerId simulatedPeerId, Percolator.Contracts.DeliverOpaqueMessageRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task PublishStandardPreKeyBundleToRelayAsync(PeerId simulatedPeerId, PeerId relayHostPeerId, DateTimeOffset expiresUtc, int oneTimeKeyCount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Cryptography.SessionId?> InitiateStandardHandshakeToMainByRelayPkhAsync(PeerId simulatedPeerId, PeerId relayHostPeerId, byte[] responderPublicKeyHash, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<byte[]> ComputePublicKeyHashAsync(PeerId simulatedPeerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Cryptography.SessionRatchetMessage> EncryptInternalEnvelopeAsync(PeerId simulatedPeerId, Percolator.Cryptography.SessionId sessionId, Percolator.Contracts.InternalEnvelope envelope, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Cryptography.Plaintext> DecryptSessionMessageAsync(PeerId simulatedPeerId, Percolator.Cryptography.SessionId sessionId, Percolator.Cryptography.SessionRatchetMessage message, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Contracts.EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(PeerId simulatedPeerId, byte[] opaqueBytes, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public virtual Task UpsertPendingStandardSignalHelloAsync(
        PeerId recipientPeerId,
        PeerId relayHostPeerId,
        Percolator.Contracts.HandshakeInitiatorHello hello,
        DateTimeOffset receivedUtc,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual Task<bool> TryAcceptPendingStandardSignalHelloAsync(
        PeerId recipientPeerId,
        string initiatorPkhHex,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public virtual Task SendChatMessageToMainAsync(PeerId simulatedPeerId, string content, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
