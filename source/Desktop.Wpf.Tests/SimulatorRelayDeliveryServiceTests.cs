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
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorRelayDeliveryServiceTests
{
    [Test]
    public async Task DeliverToPeerAsync_when_payload_is_valid_handshake_initiator_hello_upserts_pending_standard_hello_and_does_not_forward()
    {
        // Arrange
        var relayHostPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var recipientPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var ackId = Guid.NewGuid();

        var initiatorIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorSpki = initiatorIdentity.ExportSubjectPublicKeyInfo();

        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(initiatorSpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(new byte[] { 0x01 }),
            SignedPreKeyId = ByteString.CopyFrom(new byte[] { 0x02 })
        };
        var opaqueBytes = hello.ToByteArray();

        var state = new Mock<ISimulatorStateService>();

        var selfRepo = new Mock<ISelfIdentityRepository>();
        var keysStore = new Mock<ISelfIdentityKeysStore>();

        var diagnostics = new Mock<ISimulatorDiagnosticsService>();
        var logger = Mock.Of<ILogger<SimulatorRelayDeliveryService>>();

        var sut = new SimulatorRelayDeliveryService(state.Object, selfRepo.Object, keysStore.Object, logger, diagnostics.Object);

        // Act
        await sut.DeliverToPeerAsync(relayHostPeerId, recipientPeerId, ackId, opaqueBytes, debugType: "hello", CancellationToken.None);

        // Assert
        state.Verify(s => s.UpsertPendingStandardSignalHelloAsync(
            recipientPeerId,
            relayHostPeerId,
            It.Is<HandshakeInitiatorHello>(h => h.InitiatorIdentityKeySpki.ToByteArray().SequenceEqual(initiatorSpki)),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);

        state.Verify(s => s.ReceiveRelayedOpaquePayloadAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        state.Verify(s => s.EnqueueRelayUpstreamToMainAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        state.Verify(s => s.EnqueueRelayDownstreamToPeerAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<Percolator.Identity.IdentityPublicKeyHash>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DeliverToPeerAsync_when_payload_is_valid_handshake_initiator_hello_upserts_pending_standard_hello_even_if_initiator_identity_matches_a_simulated_peer()
    {
        // Arrange
        var relayHostPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var recipientPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var ackId = Guid.NewGuid();

        using var initiatorIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorSpki = initiatorIdentity.ExportSubjectPublicKeyInfo();

        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(initiatorSpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(new byte[] { 0x01 }),
            SignedPreKeyId = ByteString.CopyFrom(new byte[] { 0x02 })
        };
        var opaqueBytes = hello.ToByteArray();

        var state = new Mock<ISimulatorStateService>();

        

        var diagnostics = new Mock<ISimulatorDiagnosticsService>();
        var logger = Mock.Of<ILogger<SimulatorRelayDeliveryService>>();

        var selfRepo = new Mock<ISelfIdentityRepository>();
        var keysStore = new Mock<ISelfIdentityKeysStore>();

        var sut = new SimulatorRelayDeliveryService(state.Object, selfRepo.Object, keysStore.Object, logger, diagnostics.Object);

        // Act
        await sut.DeliverToPeerAsync(relayHostPeerId, recipientPeerId, ackId, opaqueBytes, debugType: "hello", CancellationToken.None);

        // Assert
        state.Verify(s => s.UpsertPendingStandardSignalHelloAsync(
            recipientPeerId,
            relayHostPeerId,
            It.Is<HandshakeInitiatorHello>(h => h.InitiatorIdentityKeySpki.ToByteArray().SequenceEqual(initiatorSpki)),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);

        state.Verify(s => s.ReceiveRelayedOpaquePayloadAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        state.Verify(s => s.EnqueueRelayUpstreamToMainAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        state.Verify(s => s.EnqueueRelayDownstreamToPeerAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<Percolator.Identity.IdentityPublicKeyHash>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DeliverToPeerAsync_when_initiator_pkh_matches_neither_simulated_peer_nor_main_identity_emits_routing_failure_and_does_not_enqueue()
    {
        // Arrange
        var relayHostPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var recipientPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var ackId = Guid.NewGuid();

        using var initiatorIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorSpki = initiatorIdentity.ExportSubjectPublicKeyInfo();
        var initiatorPkh = SHA256.HashData(initiatorSpki);

        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(initiatorSpki),
            InitiatorEphemeralKeySpki = ByteString.Empty,
            SignedPreKeyId = ByteString.Empty
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
            .Setup(s => s.TryGetPeerIdByIdentityPublicKeyHashAsync(It.Is<IdentityPublicKeyHash>(pkh => pkh.Equals(Percolator.Identity.IdentityPublicKeyHash.FromBytes(initiatorPkh))), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Network.PeerId?)null);

        var selfRepo = new Mock<ISelfIdentityRepository>();
        selfRepo
            .Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SelfIdentity>());

        var keysStore = new Mock<ISelfIdentityKeysStore>();

        var diagnostics = new Mock<ISimulatorDiagnosticsService>();
        var logger = Mock.Of<ILogger<SimulatorRelayDeliveryService>>();

        var sut = new SimulatorRelayDeliveryService(state.Object, selfRepo.Object, keysStore.Object, logger, diagnostics.Object);

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

        state.Verify(s => s.UpsertPendingStandardSignalHelloAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<Percolator.Network.PeerId>(), It.IsAny<HandshakeInitiatorHello>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        state.Verify(s => s.EnqueueRelayUpstreamToMainAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        state.Verify(s => s.EnqueueRelayDownstreamToPeerAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<Percolator.Identity.IdentityPublicKeyHash>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
