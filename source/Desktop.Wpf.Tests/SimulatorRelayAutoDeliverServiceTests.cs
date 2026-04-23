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
public sealed class SimulatorRelayAutoDeliverServiceTests
{
    private sealed class DelayStub : ISimulatorDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

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
            => Task.FromResult(ResolvePkhToPeerId);

        public Task<bool> DeleteRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
        {
            var relay = _relays.FirstOrDefault(r => r.RelayHostPeerId.Value == relayHostPeerId.Value);
            if (relay is null) return Task.FromResult(false);
            return Task.FromResult(relay.RemoveMessage(ackId));
        }

        public Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(PeerId relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, Guid ackId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task EnqueueRelayUpstreamToMainAsync(PeerId relayHostPeerId, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task EnqueueRelayDownstreamToPeerAsync(PeerId relayHostPeerId, byte[] targetPkh, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(PeerId relayHostPeerId, byte[] targetPkh, int max, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> ForwardRelayUpstreamToMainAsync(PeerId relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, int max, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> MoveRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CorruptRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
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
    public async Task Start_delivers_one_inbound_message_and_deletes_it_without_sleeping()
    {
        // Arrange
        var relayHostPeerIdGuid = Guid.NewGuid();
        var relayHostPeerId = new PeerId(relayHostPeerIdGuid);
        var recipientPeerIdGuid = Guid.NewGuid();
        var recipientPeerId = new PeerId(recipientPeerIdGuid);
        var ackId = Guid.NewGuid();
        var targetPkh = System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray());

        var relay = new SimulatedRelayModel(relayHostPeerId);
        relay.AutoDeliverEnabled.Value = true;
        relay.EnqueueMessage(new InboundRelayMessage(
            AckId: ackId,
            TargetPkh: targetPkh,
            OpaqueBytes: new byte[] { 0x01 },
            EnqueuedUtc: DateTimeOffset.UtcNow,
            DebugType: "x"));

        var state = new StateStub { ResolvePkhToPeerId = recipientPeerId };
        state.AddRelay(relay);

        var deliveredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var delivery = new Mock<ISimulatorRelayDeliveryService>();
        delivery
            .Setup(d => d.DeliverToPeerAsync(relayHostPeerId, recipientPeerId, ackId, It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => deliveredTcs.TrySetResult())
            .Returns(Task.CompletedTask);

        var diagnostics = Mock.Of<ISimulatorDiagnosticsService>();
        var logger = Mock.Of<ILogger<SimulatorRelayAutoDeliverService>>();
        var delay = new DelayStub();

        var sut = new SimulatorRelayAutoDeliverService(state, delivery.Object, diagnostics, delay, logger);

        // Act
        sut.Start();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await deliveredTcs.Task.WaitAsync(timeoutCts.Token);

        sut.Stop();

        // Assert
        delivery.Verify(d => d.DeliverToPeerAsync(
            relayHostPeerId,
            recipientPeerId,
            ackId,
            It.IsAny<byte[]>(),
            "x",
            It.IsAny<CancellationToken>()), Times.Once);

        relay.MessageQueue.Count.Should().Be(0);
    }
}
