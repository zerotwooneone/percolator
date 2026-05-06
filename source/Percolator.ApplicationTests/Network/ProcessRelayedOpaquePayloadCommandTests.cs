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
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Loose);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Loose);
        var directSessionRepository = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Loose);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Loose);
        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Loose);
        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Loose);

        // Not a SessionRatchetMessage; handler should now fail fast.
        var payload = Percolator.Network.Payload.FromBytes(new byte[] { 0x01, 0x02, 0x03 });
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            transport.Object,
            directSessions.Object,
            directSessionRepository.Object,
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
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Loose);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Loose);
        var directSessionRepository = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Loose);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Loose);
        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Loose);
        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Loose);

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

        var payload = Percolator.Network.Payload.FromBytes(req.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            transport.Object,
            directSessions.Object,
            directSessionRepository.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object,
            finalize.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
    }

    [Test]
    public async Task NonRatchetPayload_InviteHandshakeResponse_routes_to_invite_response_ingress()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Loose);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Loose);
        var directSessionRepository = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Loose);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Loose);
        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Loose);
        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Loose);

        var resp = new InviteHandshakeResponse
        {
            Version = 1,
            RequestCorrelationId = Guid.NewGuid().ToString(),
            AcceptorIdentityKey = ByteString.CopyFrom(new byte[] { 0x01 }),
            AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x02 }),
            InitialRatchetMessage = ByteString.CopyFrom(new byte[] { 0x03, 0x04 })
        };

        inviteIngress.Setup(x => x.HandleAsync(It.IsAny<SelfId>(), It.IsAny<InviteHandshakeResponse>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var payload = Percolator.Network.Payload.FromBytes(resp.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            transport.Object,
            directSessions.Object,
            directSessionRepository.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object,
            finalize.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
    }

    [Test]
    public async Task NonRatchetPayload_HandshakeInitiatorHello_routes_to_standard_handshake_ingress_and_does_not_enqueue_when_no_response_payload()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Loose);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Loose);
        var directSessionRepository = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Loose);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Loose);

        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Loose);
        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Loose);
        standardIngress
            .Setup(x => x.HandleAsync(It.IsAny<SelfId>(), It.IsAny<EstablishSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EstablishSessionResponse { Version = 1, Never = new EstablishSessionResponse.Types.Never { Version = 1 } });

        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(new byte[] { 0x10 }),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(new byte[] { 0x20 }),
            SignedPreKeyId = ByteString.CopyFromUtf8("spk-1")
        };

        var payload = Percolator.Network.Payload.FromBytes(hello.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            transport.Object,
            directSessions.Object,
            directSessionRepository.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object,
            finalize.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
    }

    [Test]
    public async Task NonRatchetPayload_HandshakeInitiatorHello_enqueues_establish_session_response_back_to_relay_host_when_response_payload_present()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Loose);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Loose);
        var directSessionRepository = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Loose);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Loose);

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

        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Loose);
        standardIngress
            .Setup(x => x.HandleAsync(It.IsAny<SelfId>(), It.IsAny<EstablishSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Loose);

        var helloSpki = new byte[] { 0x10, 0x11, 0x12, 0x13 };
        var expectedPkh = SHA256.HashData(helloSpki);

        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(helloSpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(new byte[] { 0x20 }),
            SignedPreKeyId = ByteString.CopyFromUtf8("spk-1")
        };

        var payload = Percolator.Network.Payload.FromBytes(hello.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());
        var selfId = new SelfId(123);
        var directSessionId = new DirectSessionId(Guid.NewGuid());

        directSessions
            .Setup(x => x.GetAsync(relayHost, selfId.Value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(directSessionId);

        Plaintext? capturedPlaintext = null;
        var cipher = SessionRatchetMessage.FromBytes(new byte[] { 0xC1, 0xC2 });
        secure
            .Setup(x => x.EncryptAsync(new SessionId(directSessionId.Value), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .Callback<SessionId, Plaintext, CancellationToken>((_, pt, _) => capturedPlaintext = pt)
            .ReturnsAsync(cipher);

        transport
            .Setup(x => x.SendMessageAsync(relayHost, directSessionId, cipher, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse { OriginalResponse = new DeliverOpaqueMessageResponse { Version = 1 } });

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            transport.Object,
            directSessions.Object,
            directSessionRepository.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object,
            finalize.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(selfId, payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);

        Assert.That(capturedPlaintext, Is.Not.Null);
        var env = InternalEnvelope.Parser.ParseFrom(capturedPlaintext!.ToArray());
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
        var mediator = new Mock<IMediator>(MockBehavior.Loose);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Loose);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var directSessions = new Mock<Percolator.Application.Services.IDirectSessionLocator>(MockBehavior.Loose);
        var directSessionRepository = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Loose);
        var inviteIngress = new Mock<IInviteHandshakeResponseIngress>(MockBehavior.Loose);
        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Loose);

        var expected = SessionId.NewId();
        var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Loose);
        finalize
            .Setup(x => x.TryFinalizeFromEstablishSessionResponseAsync(
                It.IsAny<SelfId>(),
                It.IsAny<EstablishSessionResponse>(),
                It.IsAny<Percolator.Identity.PeerId?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

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

        var payload = Percolator.Network.Payload.FromBytes(resp.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(
            logger,
            mediator.Object,
            secure.Object,
            transport.Object,
            directSessions.Object,
            directSessionRepository.Object,
            establish.Object,
            inviteIngress.Object,
            standardIngress.Object,
            finalize.Object);

        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(new SelfId(1), payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
    }
}
