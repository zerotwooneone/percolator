using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedRelayQueuePanelViewModelTests
{
    private sealed class StateStub : ISimulatorStateService
    {
        private readonly ObservableList<SimulatedPeerModel> _peers = new();
        private readonly ObservableList<SimulatedRelayModel> _relays = new();
        private readonly ObservableList<PeerRelationship> _relationships = new();

        public IReadOnlyObservableList<SimulatedPeerModel> Peers => _peers;
        public IReadOnlyObservableList<SimulatedRelayModel> Relays => _relays;
        public IReadOnlyObservableList<PeerRelationship> Relationships => _relationships;

        public PeerId? ResolvePkhToPeerId { get; set; }

        public void AddRelay(SimulatedRelayModel relay) => _relays.Add(relay);

        public Task<PeerId?> TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ResolvePkhToPeerId);
        }

        public Task<bool> DeleteRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relay = _relays.FirstOrDefault(r => r.RelayHostPeerId.Value == relayHostPeerId.Value);
            if (relay is null) return Task.FromResult(false);
            return Task.FromResult(relay.RemoveMessage(ackId));
        }

        public Task<bool> MoveRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CorruptRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task EnqueueRelayUpstreamToMainAsync(PeerId relayHostPeerId, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task EnqueueRelayDownstreamToPeerAsync(PeerId relayHostPeerId, byte[] targetPkh, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(PeerId relayHostPeerId, byte[] targetPkh, int max, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> ForwardRelayUpstreamToMainAsync(PeerId relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, int max, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(PeerId relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, Guid ackId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<PeerId> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RemovePeerAsync(PeerId peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddPublishedKeysRelationshipAsync(PeerId publisherPeerId, PeerId hostPeerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RemovePublishedKeysRelationshipAsync(PeerId publisherPeerId, PeerId hostPeerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddRelayActiveSessionAsync(PeerId relayHostPeerId, PeerId peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RemoveRelayActiveSessionAsync(PeerId relayHostPeerId, PeerId peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Percolator.Contracts.EstablishDirectSessionResponse> ReceiveEstablishDirectSessionFromMainAsync(PeerId simulatedPeerId, PeerId inviterPeerId, Percolator.Contracts.EstablishDirectSessionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
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

        public Task UpsertPendingStandardSignalHelloAsync(
            PeerId recipientPeerId,
            PeerId relayHostPeerId,
            Percolator.Contracts.HandshakeInitiatorHello hello,
            DateTimeOffset receivedUtc,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryAcceptPendingStandardSignalHelloAsync(
            PeerId recipientPeerId,
            string initiatorPkhHex,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task SendChatMessageToMainAsync(PeerId simulatedPeerId, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Test]
    public async Task DeliverNextAsync_when_downstream_message_routes_to_peer_and_deletes_message()
    {
        // Arrange
        var relayHostPeerIdGuid = Guid.NewGuid();
        var relayHostPeerId = new PeerId(relayHostPeerIdGuid);
        var recipientPeerIdGuid = Guid.NewGuid();
        var recipientPeerId = new PeerId(recipientPeerIdGuid);
        var ackId = Guid.NewGuid();
        var targetPkh = System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray());
        var opaque = new byte[] { 0x01, 0x02, 0x03 };

        var relay = new SimulatedRelayModel(relayHostPeerId);
        relay.EnqueueMessage(new InboundRelayMessage(
            AckId: ackId,
            TargetPkh: targetPkh,
            OpaqueBytes: opaque,
            EnqueuedUtc: DateTimeOffset.UtcNow,
            DebugType: "t"));

        var state = new StateStub { ResolvePkhToPeerId = recipientPeerId };
        state.AddRelay(relay);

        var delivery = new Mock<ISimulatorRelayDeliveryService>();
        var diagnostics = Mock.Of<ISimulatorDiagnosticsService>();
        var ui = new TestUiDispatcher();
        var logger = Mock.Of<ILogger<SimulatedRelayQueuePanelViewModel>>();

        using var sut = new SimulatedRelayQueuePanelViewModel(
            relayHostPeerId: relayHostPeerId,
            relayHostName: "relay",
            peerNameById: _ => "p",
            getRelayHostToMainSessionId: () => Task.FromResult<Percolator.Cryptography.SessionId?>(null),
            ui: ui,
            state: state,
            delivery: delivery.Object,
            diagnostics: diagnostics,
            logger: logger);

        // Act
        await sut.DeliverNextAsync(CancellationToken.None);

        // Assert
        delivery.Verify(d => d.DeliverToPeerAsync(
            relayHostPeerId,
            recipientPeerId,
            ackId,
            It.Is<byte[]>(b => b.SequenceEqual(opaque)),
            "t",
            It.IsAny<CancellationToken>()), Times.Once);

        relay.MessageQueue.Count.Should().Be(0);
    }
}
