using System;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Application.Ingress;
using Percolator.Application.Identity;
using Percolator.Application.Network;


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

        public void AddRelay(Guid relayHostPeerId, bool autoDeliverEnabled)
        {
            var relay = new SimulatedRelayModel(relayHostPeerId);
            relay.AutoDeliverEnabled.Value = autoDeliverEnabled;
            _relays.Add(relay);
        }

        public System.Threading.Tasks.Task<Guid> AddPeerAsync(string? displayName, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task RemovePeerAsync(Guid peerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Guid?> TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task EnqueueRelayUpstreamToMainAsync(Guid relayHostPeerId, byte[] opaqueBytes, string? debugType = null, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task EnqueueRelayDownstreamToPeerAsync(Guid relayHostPeerId, byte[] targetPkh, byte[] opaqueBytes, string? debugType = null, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(Guid relayHostPeerId, byte[] targetPkh, int max, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<int> ForwardRelayUpstreamToMainAsync(Guid relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, int max, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(Guid relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, Guid ackId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<bool> DeleteRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<bool> MoveRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, int delta, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<bool> CorruptRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task AddPublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task RemovePublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task AddRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task RemoveRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Contracts.EstablishDirectSessionResponse> ReceiveEstablishDirectSessionFromMainAsync(Guid simulatedPeerId, Guid inviterPeerId, Percolator.Contracts.EstablishDirectSessionRequest request, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<SimulatedPeerInviteAcceptance> AcceptReverseSignalInviteAsync(Guid simulatedPeerId, Guid inviterPeerId, Percolator.Contracts.EstablishDirectSessionRequest invite, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task DeliverInviteHandshakeResponseToMainAsync(Percolator.Contracts.InviteHandshakeResponse response, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task ReceiveInviteHandshakeResponseFromMainAsync(Guid simulatedPeerId, Percolator.Contracts.InviteHandshakeResponse response, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task QueueInviteHandshakeResponseForDeliveryToMainAsync(Guid simulatedPeerId, Guid requestCorrelationId, Percolator.Contracts.InviteHandshakeResponse response, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<bool> TryDeliverQueuedInviteHandshakeResponseToMainAsync(Guid simulatedPeerId, Guid requestCorrelationId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Cryptography.SessionId?> TryFinalizeInviteHandshakeResponseFromMainAsync(Guid simulatedPeerId, Guid acceptorPeerId, Guid requestCorrelationId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Contracts.EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(Guid simulatedPeerId, Percolator.Contracts.EstablishSessionRequest request, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Contracts.DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(Guid simulatedPeerId, Percolator.Contracts.DeliverOpaqueMessageRequest request, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task PublishStandardPreKeyBundleToRelayAsync(Guid simulatedPeerId, Guid relayHostPeerId, DateTimeOffset expiresUtc, bool includeOneTimeKeys, int oneTimeKeyCount, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Cryptography.SessionId?> InitiateStandardHandshakeToMainByRelayPkhAsync(Guid simulatedPeerId, Guid relayHostPeerId, byte[] responderPublicKeyHash, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<byte[]> ComputePublicKeyHashAsync(Guid simulatedPeerId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Cryptography.SessionRatchetMessage> EncryptInternalEnvelopeAsync(Guid simulatedPeerId, Percolator.Cryptography.SessionId sessionId, Percolator.Contracts.InternalEnvelope envelope, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Cryptography.Plaintext> DecryptSessionMessageAsync(Guid simulatedPeerId, Percolator.Cryptography.SessionId sessionId, Percolator.Cryptography.SessionRatchetMessage message, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.Task<Percolator.Contracts.EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(Guid simulatedPeerId, byte[] opaqueBytes, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public System.Threading.Tasks.Task UpsertPendingStandardSignalHelloAsync(
            Guid recipientPeerId,
            Guid relayHostPeerId,
            Percolator.Contracts.HandshakeInitiatorHello hello,
            DateTimeOffset receivedUtc,
            System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task<bool> TryAcceptPendingStandardSignalHelloAsync(
            Guid recipientPeerId,
            string initiatorPkhHex,
            System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult(false);
    }

    [Test]
    public void GlobalAutoRelayAll_sets_AutoDeliverEnabled_on_all_relays()
    {
        // Arrange
        var state = new StateStub();
        state.AddRelay(Guid.NewGuid(), autoDeliverEnabled: false);
        state.AddRelay(Guid.NewGuid(), autoDeliverEnabled: false);

        var directory = Mock.Of<ISimulatorInitializer>();
        var messageService = new PercolatorMessageService(
            logger: Mock.Of<ILogger<PercolatorMessageService>>(),
            messageIngress: Mock.Of<IMessageIngress>(),
            establishService: Mock.Of<IEstablishDirectSessionService>(),
            inviteHandshakeResponseIngress: Mock.Of<IInviteHandshakeResponseIngress>(),
            standardHandshakeIngress: Mock.Of<IStandardHandshakeIngress>(),
            active: new ActiveIdentityContext());
        var delivery = Mock.Of<ISimulatorRelayDeliveryService>();
        var diagnostics = Mock.Of<ISimulatorDiagnosticsService>();
        var active = new ActiveIdentityContext();
        var ui = new TestUiDispatcher();
        var logger = Mock.Of<ILogger<SimulatorRelayTabViewModel>>();
        var loggerFactory = Mock.Of<ILoggerFactory>();

        using var sut = new SimulatorRelayTabViewModel(
            state: state,
            directory: directory,
            messageService: messageService,
            delivery: delivery,
            diagnostics: diagnostics,
            active: active,
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
