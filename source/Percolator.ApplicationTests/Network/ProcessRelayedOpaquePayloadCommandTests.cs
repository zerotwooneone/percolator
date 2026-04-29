using System.Security.Cryptography;
using Google.Protobuf;
using MediatR;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

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
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Strict);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Strict);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Strict);
        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Strict);
        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Strict);

        // Not a SessionRatchetMessage; handler should now fail fast.
        var payload = new Percolator.Network.Payload(new byte[] { 0x01, 0x02, 0x03 });
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            transport.Object,
            directSessions.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object,
            finalize.Object);
        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.False);
    }

    [Test]
    public async Task NonRatchetPayload_EstablishDirectSessionRequest_routes_to_queue_invite_as_relayed()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Strict);
        var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);
        var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "t", null) { SelfIdentityId = new SelfId(1)} };
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Strict);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Strict);
        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Strict);
        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Strict);

        var req = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(new byte[] { 0x10, 0x11, 0x12 }),
            Payload = ByteString.CopyFrom(new byte[] { 0x20, 0x21 }),
            PayloadSignature = ByteString.CopyFrom(new byte[] { 0x30 })
        };

        establish.Setup(x => x.QueueInviteAsync(
                It.IsAny<SelfId>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                true,
                It.IsAny<Percolator.Identity.PeerId?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Percolator.Cryptography.Primitives.RequestCorrelationId(Guid.NewGuid()));

        var payload = new Percolator.Network.Payload(req.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            transport.Object,
            directSessions.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object,
            finalize.Object);

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
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Strict);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Strict);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Strict);
        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Strict);
        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Strict);

        var resp = new InviteHandshakeResponse
        {
            Version = 1,
            RequestCorrelationId = Guid.NewGuid().ToString(),
            AcceptorIdentityKey = ByteString.CopyFrom(new byte[] { 0x01 }),
            AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x02 }),
            InitialRatchetMessage = ByteString.CopyFrom(new byte[] { 0x03, 0x04 })
        };

        inviteIngress.Setup(x => x.HandleAsync(It.IsAny<SelfId>(), It.IsAny<InviteHandshakeResponse>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var payload = new Percolator.Network.Payload(resp.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            transport.Object,
            directSessions.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object,
            finalize.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
        inviteIngress.VerifyAll();
    }

    [Test]
    public async Task NonRatchetPayload_HandshakeInitiatorHello_routes_to_standard_handshake_ingress_and_does_not_enqueue_when_no_response_payload()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Strict);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Strict);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Strict);

        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Strict);
        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Strict);
        standardIngress
            .Setup(x => x.HandleAsync(It.IsAny<SelfId>(), It.IsAny<EstablishSessionRequest>(), It.IsAny<CancellationToken>()))
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
            transport.Object,
            directSessions.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object,
            finalize.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
        standardIngress.VerifyAll();
        directSessions.VerifyNoOtherCalls();
        secure.VerifyNoOtherCalls();
        transport.VerifyNoOtherCalls();
    }

    [Test]
    public async Task NonRatchetPayload_HandshakeInitiatorHello_enqueues_establish_session_response_back_to_relay_host_when_response_payload_present()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Strict);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Strict);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Strict);

        var response = new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                ResponsePayload = ByteString.CopyFrom(new byte[] { 0xAA, 0xBB }),
                IdentitySigningKey = ByteString.CopyFrom(new byte[] { 0x01 }),
                PayloadSignature = ByteString.CopyFrom(new byte[] { 0x02 })
            }
        };

        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Strict);
        standardIngress
            .Setup(x => x.HandleAsync(It.IsAny<SelfId>(), It.IsAny<EstablishSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response)
            .Verifiable();

        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Strict);

        var helloSpki = new byte[] { 0x10, 0x11, 0x12, 0x13 };
        var expectedPkh = SHA256.HashData(helloSpki);

        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(helloSpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(new byte[] { 0x20 }),
            SignedPreKeyId = ByteString.CopyFromUtf8("spk-1")
        };

        var payload = new Percolator.Network.Payload(hello.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());
        var selfId = new SelfId(123);
        var directSessionId = new DirectSessionId(Guid.NewGuid());

        directSessions
            .Setup(x => x.GetAsync(relayHost, selfId.Value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(directSessionId)
            .Verifiable();

        Plaintext? capturedPlaintext = null;
        var cipher = new SessionRatchetMessage(new byte[] { 0xC1, 0xC2 });
        secure
            .Setup(x => x.EncryptAsync(new SessionId(directSessionId.Value), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .Callback<SessionId, Plaintext, CancellationToken>((_, pt, _) => capturedPlaintext = pt)
            .ReturnsAsync(cipher)
            .Verifiable();

        transport
            .Setup(x => x.SendMessageAsync(relayHost, directSessionId, cipher, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse { OriginalResponse = new DeliverOpaqueMessageResponse { Version = 1 } })
            .Verifiable();

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            transport.Object,
            directSessions.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object,
            finalize.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(selfId, payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
        standardIngress.VerifyAll();
        directSessions.VerifyAll();
        secure.VerifyAll();
        transport.VerifyAll();

        Assert.That(capturedPlaintext, Is.Not.Null);
        var env = InternalEnvelope.Parser.ParseFrom(capturedPlaintext!.Value);
        Assert.That(env.ApplicationPayloadCase, Is.EqualTo(InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope));
        Assert.That(env.MessageQueueEnvelope.MessageCase, Is.EqualTo(MessageQueueEnvelope.MessageOneofCase.EnqueueOpaqueMessageRequest));

        var req = env.MessageQueueEnvelope.EnqueueOpaqueMessageRequest;
        Assert.That(req.HasRecipientPublicKeyHash, Is.True);
        Assert.That(req.RecipientPublicKeyHash.ToByteArray(), Is.EqualTo(expectedPkh));

        Assert.That(req.HasMessageBlob, Is.True);
        Assert.That(req.MessageBlob.ToByteArray(), Is.EqualTo(response.ToByteArray()));
    }

    [Test]
    public async Task NonRatchetPayload_EstablishSessionResponse_routes_to_initiator_finalize_service()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Strict);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Strict);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Strict);
        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Strict);

        var expected = SessionId.NewId();
        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Strict);
        finalize
            .Setup(x => x.TryFinalizeFromEstablishSessionResponseAsync(
                It.IsAny<SelfId>(),
                It.IsAny<EstablishSessionResponse>(),
                It.IsAny<Percolator.Identity.PeerId?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected)
            .Verifiable();

        var resp = new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                IdentitySigningKey = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
                ResponsePayload = ByteString.CopyFrom(new byte[] { 0xAA }),
                PayloadSignature = ByteString.CopyFrom(new byte[] { 0xBB })
            }
        };

        var payload = new Percolator.Network.Payload(resp.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            transport.Object,
            directSessions.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object,
            finalize.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
        finalize.VerifyAll();
    }
}
