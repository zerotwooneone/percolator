using System;
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
public sealed class SimulatorRelayTabViewModelTests
{
    private sealed class StateStub : ISimulatorStateService
    {
        private readonly ObservableList<SimulatedPeerModel> _peers = new();
        private readonly ObservableList<SimulatedRelayModel> _relays = new();
        private readonly ObservableList<PeerRelationship> _relationships = new();

        public IReadOnlyObservableList<SimulatedPeerModel> Peers => _peers;
        public IReadOnlyObservableList<SimulatedRelayModel> Relays => _relays;
        public IReadOnlyObservableList<PeerRelationship> Relationships => _relationships;

        public void AddRelay(PeerId relayHostPeerId, bool autoDeliverEnabled)
        {
            var relay = new SimulatedRelayModel(relayHostPeerId);
            relay.AutoDeliverEnabled.Value = autoDeliverEnabled;
            _relays.Add(relay);
        }

        public System.Threading.Tasks.Task<PeerId> AddPeerAsync(string? displayName, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task RemovePeerAsync(PeerId peerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<PeerId?> TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task EnqueueRelayUpstreamToMainAsync(PeerId relayHostPeerId, byte[] opaqueBytes, string? debugType = null, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task EnqueueRelayDownstreamToPeerAsync(PeerId relayHostPeerId, byte[] targetPkh, byte[] opaqueBytes, string? debugType = null, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(PeerId relayHostPeerId, byte[] targetPkh, int max, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<int> ForwardRelayUpstreamToMainAsync(PeerId relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, int max, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(PeerId relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, Guid ackId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<bool> DeleteRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<bool> MoveRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, int delta, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<bool> CorruptRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task AddPublishedKeysRelationshipAsync(PeerId publisherPeerId, PeerId hostPeerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task RemovePublishedKeysRelationshipAsync(PeerId publisherPeerId, PeerId hostPeerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task AddRelayActiveSessionAsync(PeerId relayHostPeerId, PeerId peerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task RemoveRelayActiveSessionAsync(PeerId relayHostPeerId, PeerId peerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Contracts.EstablishDirectSessionResponse> ReceiveEstablishDirectSessionFromMainAsync(PeerId simulatedPeerId, PeerId inviterPeerId, Percolator.Contracts.EstablishDirectSessionRequest request, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<SimulatedPeerInviteAcceptance> AcceptReverseSignalInviteAsync(PeerId simulatedPeerId, PeerId inviterPeerId, Percolator.Contracts.EstablishDirectSessionRequest invite, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task DeliverInviteHandshakeResponseToMainAsync(Percolator.Contracts.InviteHandshakeResponse response, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task ReceiveInviteHandshakeResponseFromMainAsync(PeerId simulatedPeerId, Percolator.Contracts.InviteHandshakeResponse response, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task QueueInviteHandshakeResponseForDeliveryToMainAsync(PeerId simulatedPeerId, Guid requestCorrelationId, Percolator.Contracts.InviteHandshakeResponse response, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<bool> TryDeliverQueuedInviteHandshakeResponseToMainAsync(PeerId simulatedPeerId, Guid requestCorrelationId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Cryptography.SessionId?> TryFinalizeInviteHandshakeResponseFromMainAsync(PeerId simulatedPeerId, PeerId acceptorPeerId, Guid requestCorrelationId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Contracts.EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(PeerId simulatedPeerId, Percolator.Contracts.EstablishSessionRequest request, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Contracts.DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(PeerId simulatedPeerId, Percolator.Contracts.DeliverOpaqueMessageRequest request, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task PublishStandardPreKeyBundleToRelayAsync(PeerId simulatedPeerId, PeerId relayHostPeerId, DateTimeOffset expiresUtc, int oneTimeKeyCount, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Cryptography.SessionId?> InitiateStandardHandshakeToMainByRelayPkhAsync(PeerId simulatedPeerId, PeerId relayHostPeerId, byte[] responderPublicKeyHash, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<byte[]> ComputePublicKeyHashAsync(PeerId simulatedPeerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Cryptography.SessionRatchetMessage> EncryptInternalEnvelopeAsync(PeerId simulatedPeerId, Percolator.Cryptography.SessionId sessionId, Percolator.Contracts.InternalEnvelope envelope, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Cryptography.Plaintext> DecryptSessionMessageAsync(PeerId simulatedPeerId, Percolator.Cryptography.SessionId sessionId, Percolator.Cryptography.SessionRatchetMessage message, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Contracts.EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(PeerId simulatedPeerId, byte[] opaqueBytes, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public System.Threading.Tasks.Task UpsertPendingStandardSignalHelloAsync(
            PeerId recipientPeerId,
            PeerId relayHostPeerId,
            Percolator.Contracts.HandshakeInitiatorHello hello,
            DateTimeOffset receivedUtc,
            System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task<bool> TryAcceptPendingStandardSignalHelloAsync(
            PeerId recipientPeerId,
            string initiatorPkhHex,
            System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult(false);

        public System.Threading.Tasks.Task SendChatMessageToMainAsync(PeerId simulatedPeerId, string content, System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.CompletedTask;
    }

    [Test]
    public void GlobalAutoRelayAll_sets_AutoDeliverEnabled_on_all_relays()
    {
        // Arrange
        var state = new StateStub();
        state.AddRelay(new PeerId(Guid.NewGuid()), autoDeliverEnabled: false);
        state.AddRelay(new PeerId(Guid.NewGuid()), autoDeliverEnabled: false);

        var delivery = Mock.Of<ISimulatorRelayDeliveryService>();
        var diagnostics = Mock.Of<ISimulatorDiagnosticsService>();
        var ui = new TestUiDispatcher();
        var logger = Mock.Of<ILogger<SimulatorRelayTabViewModel>>();
        var loggerFactory = Mock.Of<ILoggerFactory>();

        using var sut = new SimulatorRelayTabViewModel(
            state: state,
            delivery: delivery,
            diagnostics: diagnostics,
            ui: ui,
            logger: logger,
            loggerFactory: loggerFactory);

        // Act
        sut.GlobalAutoRelayAll.Value = true;

        // Assert
        state.Relays.Should().HaveCount(2);
        state.Relays.Should().OnlyContain(r => r.AutoDeliverEnabled.CurrentValue);
        sut.RelayPanels.Count.Should().Be(2);
    }
}
