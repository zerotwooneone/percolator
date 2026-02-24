using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class ProcessRelayedOpaquePayloadCommandTests
{
    [Test]
    public async Task NonRatchetPayload_returns_failure_and_does_not_call_any_ingress()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Strict);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Strict);
        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Strict);

        // Not a SessionRatchetMessage; handler should now fail fast.
        var payload = new Percolator.Network.Payload(new byte[] { 0x01, 0x02, 0x03 });
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object);
        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.False);
    }

    [Test]
    public async Task NonRatchetPayload_EstablishDirectSessionRequest_routes_to_queue_invite_as_relayed()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);
        var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "t", null) { SelfIdentityId = new SelfId(1)} };
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Strict);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Strict);
        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Strict);

        var req = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(new byte[] { 0x10, 0x11, 0x12 }),
            Payload = ByteString.CopyFrom(new byte[] { 0x20, 0x21 }),
            PayloadSignature = ByteString.CopyFrom(new byte[] { 0x30 })
        };

        establish.Setup(x => x.QueueInviteAsync(
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Percolator.Cryptography.Primitives.RequestCorrelationId(Guid.NewGuid()));

        var payload = new Percolator.Network.Payload(req.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
        establish.VerifyAll();
    }

    [Test]
    public async Task NonRatchetPayload_InviteHandshakeResponse_routes_to_invite_response_ingress()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Strict);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Strict);
        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Strict);

        var resp = new InviteHandshakeResponse
        {
            Version = 1,
            RequestCorrelationId = Guid.NewGuid().ToString(),
            AcceptorIdentityKey = ByteString.CopyFrom(new byte[] { 0x01 }),
            AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x02 }),
            InitialRatchetMessage = ByteString.CopyFrom(new byte[] { 0x03, 0x04 })
        };

        inviteIngress.Setup(x => x.HandleAsync(It.IsAny<InviteHandshakeResponse>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var payload = new Percolator.Network.Payload(resp.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
        inviteIngress.VerifyAll();
    }

    [Test]
    public async Task NonRatchetPayload_HandshakeInitiatorHello_routes_to_standard_handshake_ingress()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Strict);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Strict);

        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Strict);
        standardIngress
            .Setup(x => x.HandleAsync(It.IsAny<EstablishSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EstablishSessionResponse { Version = 1, Never = new EstablishSessionResponse.Types.Never { Version = 1 } })
            .Verifiable();

        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(new byte[] { 0x10 }),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(new byte[] { 0x20 }),
            SignedPreKeyId = ByteString.CopyFromUtf8("spk-1")
        };

        var payload = new Percolator.Network.Payload(hello.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
        standardIngress.VerifyAll();
    }
}
