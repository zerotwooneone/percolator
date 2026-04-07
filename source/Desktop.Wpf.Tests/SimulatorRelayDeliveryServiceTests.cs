using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Contracts;
using Percolator.Application.Identity;
using Percolator.Application.Ingress;
using Percolator.Application.Network;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorRelayDeliveryServiceTests
{
    [Test]
    public async Task DeliverToPeerAsync_when_initiator_pkh_does_not_match_simulated_peer_enqueues_establish_session_response_upstream_to_main()
    {
        // Arrange
        var relayHostPeerId = Guid.NewGuid();
        var recipientPeerId = Guid.NewGuid();
        var ackId = Guid.NewGuid();

        var initiatorIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorSpki = initiatorIdentity.ExportSubjectPublicKeyInfo();
        var initiatorPkh = SHA256.HashData(initiatorSpki);

        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(initiatorSpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(new byte[] { 0x01 }),
            SignedPreKeyId = ByteString.CopyFrom(new byte[] { 0x02 })
        };
        var opaqueBytes = hello.ToByteArray();

        var forwarded = new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                IdentitySigningKey = ByteString.CopyFrom(new byte[] { 0x99 }),
                ResponsePayload = ByteString.CopyFrom(new EstablishSessionResponse.Types.Response.Types.ResponsePayload
                {
                    Version = 1,
                    SessionId = Guid.NewGuid().ToString()
                }.ToByteArray()),
                PayloadSignature = ByteString.CopyFrom(new byte[] { 0xAA })
            }
        };

        var state = new Mock<ISimulatorStateService>();
        state
            .Setup(s => s.ReceiveRelayedOpaquePayloadAsync(recipientPeerId, It.Is<byte[]>(b => b.SequenceEqual(opaqueBytes)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(forwarded);

        state
            .Setup(s => s.TryGetPeerIdByIdentityPkhAsync(It.Is<byte[]>(b => b.SequenceEqual(initiatorPkh)), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        var selfRepo = new Mock<ISelfIdentityRepository>();
        selfRepo
            .Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new SelfIdentity(new SelfId(1)) });

        var keysStore = new Mock<ISelfIdentityKeysStore>();
        keysStore
            .Setup(s => s.LoadAsync(new SelfId(1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new X3dhKeys(
                IdentitySigningKey: initiatorIdentity,
                SignedPreKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)));

        state
            .Setup(s => s.EnqueueRelayUpstreamToMainAsync(
                relayHostPeerId,
                It.IsAny<byte[]>(),
                nameof(EstablishSessionResponse),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var messageService = new PercolatorMessageService(
            logger: Mock.Of<ILogger<PercolatorMessageService>>(),
            messageIngress: Mock.Of<IMessageIngress>(),
            establishService: Mock.Of<IEstablishDirectSessionService>(),
            inviteHandshakeResponseIngress: Mock.Of<IInviteHandshakeResponseIngress>(),
            standardHandshakeIngress: Mock.Of<IStandardHandshakeIngress>(),
            active: new ActiveIdentityContext());

        var diagnostics = new Mock<ISimulatorDiagnosticsService>();
        var logger = Mock.Of<ILogger<SimulatorRelayDeliveryService>>();

        var sut = new SimulatorRelayDeliveryService(messageService, state.Object, selfRepo.Object, keysStore.Object, logger, diagnostics.Object);

        // Act
        await sut.DeliverToPeerAsync(relayHostPeerId, recipientPeerId, ackId, opaqueBytes, debugType: "hello", CancellationToken.None);

        // Assert
        state.Verify(s => s.EnqueueRelayUpstreamToMainAsync(
            relayHostPeerId,
            It.Is<byte[]>(b => b.SequenceEqual(forwarded.ToByteArray())),
            nameof(EstablishSessionResponse),
            It.IsAny<CancellationToken>()), Times.Once);

        diagnostics.Verify(d => d.Emit(
            SimulatorDiagnosticEventType.HandshakeStateTransition,
            It.Is<string>(msg => msg.Contains("response enqueued", StringComparison.OrdinalIgnoreCase)),
            recipientPeerId,
            relayHostPeerId,
            null,
            "ResponseEnqueued"), Times.Once);

        ackId.Should().NotBe(Guid.Empty);
    }

    [Test]
    public async Task DeliverToPeerAsync_when_initiator_pkh_matches_simulated_peer_enqueues_establish_session_response_downstream_to_initiator_pkh()
    {
        // Arrange
        var relayHostPeerId = Guid.NewGuid();
        var recipientPeerId = Guid.NewGuid();
        var ackId = Guid.NewGuid();

        using var initiatorIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorSpki = initiatorIdentity.ExportSubjectPublicKeyInfo();
        var initiatorPkh = SHA256.HashData(initiatorSpki);

        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(initiatorSpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(new byte[] { 0x01 }),
            SignedPreKeyId = ByteString.CopyFrom(new byte[] { 0x02 })
        };
        var opaqueBytes = hello.ToByteArray();

        var forwarded = new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                IdentitySigningKey = ByteString.CopyFrom(new byte[] { 0x99 }),
                ResponsePayload = ByteString.CopyFrom(new EstablishSessionResponse.Types.Response.Types.ResponsePayload
                {
                    Version = 1,
                    SessionId = Guid.NewGuid().ToString()
                }.ToByteArray()),
                PayloadSignature = ByteString.CopyFrom(new byte[] { 0xAA })
            }
        };

        var initiatorPeerId = Guid.NewGuid();

        var state = new Mock<ISimulatorStateService>();
        state
            .Setup(s => s.ReceiveRelayedOpaquePayloadAsync(recipientPeerId, It.Is<byte[]>(b => b.SequenceEqual(opaqueBytes)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(forwarded);

        state
            .Setup(s => s.TryGetPeerIdByIdentityPkhAsync(It.Is<byte[]>(b => b.SequenceEqual(initiatorPkh)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(initiatorPeerId);

        state
            .Setup(s => s.EnqueueRelayDownstreamToPeerAsync(
                relayHostPeerId,
                It.Is<byte[]>(b => b.SequenceEqual(initiatorPkh)),
                It.IsAny<byte[]>(),
                nameof(EstablishSessionResponse),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var messageService = new PercolatorMessageService(
            logger: Mock.Of<ILogger<PercolatorMessageService>>(),
            messageIngress: Mock.Of<IMessageIngress>(),
            establishService: Mock.Of<IEstablishDirectSessionService>(),
            inviteHandshakeResponseIngress: Mock.Of<IInviteHandshakeResponseIngress>(),
            standardHandshakeIngress: Mock.Of<IStandardHandshakeIngress>(),
            active: new ActiveIdentityContext());

        var diagnostics = new Mock<ISimulatorDiagnosticsService>();
        var logger = Mock.Of<ILogger<SimulatorRelayDeliveryService>>();

        var selfRepo = new Mock<ISelfIdentityRepository>();
        var keysStore = new Mock<ISelfIdentityKeysStore>();

        var sut = new SimulatorRelayDeliveryService(messageService, state.Object, selfRepo.Object, keysStore.Object, logger, diagnostics.Object);

        // Act
        await sut.DeliverToPeerAsync(relayHostPeerId, recipientPeerId, ackId, opaqueBytes, debugType: "hello", CancellationToken.None);

        // Assert
        state.Verify(s => s.EnqueueRelayDownstreamToPeerAsync(
            relayHostPeerId,
            It.Is<byte[]>(b => b.SequenceEqual(initiatorPkh)),
            It.Is<byte[]>(b => b.SequenceEqual(forwarded.ToByteArray())),
            nameof(EstablishSessionResponse),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task DeliverToPeerAsync_when_initiator_pkh_matches_neither_simulated_peer_nor_main_identity_emits_routing_failure_and_does_not_enqueue()
    {
        // Arrange
        var relayHostPeerId = Guid.NewGuid();
        var recipientPeerId = Guid.NewGuid();
        var ackId = Guid.NewGuid();

        using var initiatorIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorSpki = initiatorIdentity.ExportSubjectPublicKeyInfo();
        var initiatorPkh = SHA256.HashData(initiatorSpki);

        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(initiatorSpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(new byte[] { 0x01 }),
            SignedPreKeyId = ByteString.CopyFrom(new byte[] { 0x02 })
        };
        var opaqueBytes = hello.ToByteArray();

        var forwarded = new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                IdentitySigningKey = ByteString.CopyFrom(new byte[] { 0x99 }),
                ResponsePayload = ByteString.CopyFrom(new EstablishSessionResponse.Types.Response.Types.ResponsePayload
                {
                    Version = 1,
                    SessionId = Guid.NewGuid().ToString()
                }.ToByteArray()),
                PayloadSignature = ByteString.CopyFrom(new byte[] { 0xAA })
            }
        };

        var state = new Mock<ISimulatorStateService>();
        state
            .Setup(s => s.ReceiveRelayedOpaquePayloadAsync(recipientPeerId, It.Is<byte[]>(b => b.SequenceEqual(opaqueBytes)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(forwarded);
        state
            .Setup(s => s.TryGetPeerIdByIdentityPkhAsync(It.Is<byte[]>(b => b.SequenceEqual(initiatorPkh)), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        var selfRepo = new Mock<ISelfIdentityRepository>();
        selfRepo
            .Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SelfIdentity>());

        var keysStore = new Mock<ISelfIdentityKeysStore>();

        var messageService = new PercolatorMessageService(
            logger: Mock.Of<ILogger<PercolatorMessageService>>(),
            messageIngress: Mock.Of<IMessageIngress>(),
            establishService: Mock.Of<IEstablishDirectSessionService>(),
            inviteHandshakeResponseIngress: Mock.Of<IInviteHandshakeResponseIngress>(),
            standardHandshakeIngress: Mock.Of<IStandardHandshakeIngress>(),
            active: new ActiveIdentityContext());

        var diagnostics = new Mock<ISimulatorDiagnosticsService>();
        var logger = Mock.Of<ILogger<SimulatorRelayDeliveryService>>();

        var sut = new SimulatorRelayDeliveryService(messageService, state.Object, selfRepo.Object, keysStore.Object, logger, diagnostics.Object);

        // Act
        await sut.DeliverToPeerAsync(relayHostPeerId, recipientPeerId, ackId, opaqueBytes, debugType: "hello", CancellationToken.None);

        // Assert
        diagnostics.Verify(d => d.Emit(
            SimulatorDiagnosticEventType.RelayRoutingFailure,
            It.Is<string>(msg => msg.Contains("routing failure", StringComparison.OrdinalIgnoreCase)),
            null,
            relayHostPeerId,
            ackId,
            null), Times.Once);

        state.Verify(s => s.EnqueueRelayUpstreamToMainAsync(It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        state.Verify(s => s.EnqueueRelayDownstreamToPeerAsync(It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
