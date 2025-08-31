using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class DeliverOpaqueMessageHandlerTests
{
    private static (InternalEnvelope Envelope, byte[] Bytes) BuildEnvelope(Action<InternalEnvelope> configure)
    {
        var env = new InternalEnvelope();
        configure(env);
        return (env, env.ToByteArray());
    }

    private static byte[] RandomBytes(int len = 32) => RandomNumberGenerator.GetBytes(len);

    private static (SessionRatchetMessage Msg, Plaintext Plain) MakeRatchetAndPlain(byte[] plain)
    {
        // The handler treats payload as opaque; for unit tests we can wrap arbitrary bytes
        var sr = new SessionRatchetMessage(plain);
        return (sr, new Plaintext(plain));
    }

    private static DeliverOpaqueMessageHandler CreateHandler(
        out Mock<IDirectSessionManager> sessionMgr,
        out Mock<IPeerConnectionRepository> peerRepo,
        out Mock<IMediator> mediator,
        out Mock<IDirectSessionRepository> directRepo)
    {
        sessionMgr = new Mock<IDirectSessionManager>(MockBehavior.Strict);
        peerRepo = new Mock<IPeerConnectionRepository>(MockBehavior.Strict);
        mediator = new Mock<IMediator>(MockBehavior.Strict);
        directRepo = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        var logger = Mock.Of<ILogger<DeliverOpaqueMessageHandler>>();
        var active = new ActiveIdentityContext
        {
            Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Test", null) { SelfIdentityId = 1 }
        };
        return new DeliverOpaqueMessageHandler(logger, sessionMgr.Object, peerRepo.Object, mediator.Object, directRepo.Object, active);
    }

    [Test]
    public async Task Returns_empty_when_decryption_result_is_null()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo);
        var sessionId = Guid.NewGuid();
        var cmd = new DeliverOpaqueMessageCommand { SessionId = sessionId, PayloadBytes = RandomBytes(48) };
        sessionMgr.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync((Plaintext?)null);

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().BeNull();
        sessionMgr.VerifyAll();
        mediator.VerifyNoOtherCalls();
        peerRepo.VerifyNoOtherCalls();
        directRepo.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Returns_empty_when_direct_session_mapping_missing()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo);
        var sessionId = Guid.NewGuid();

        var (_, plain) = MakeRatchetAndPlain(BuildEnvelope(env =>
        {
            env.DhtEnvelope = new DhtEnvelope { PingRequest = new PingRequest() };
        }).Bytes);

        sessionMgr.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(plain);
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>())).ReturnsAsync((DirectSession?)null);

        var cmd = new DeliverOpaqueMessageCommand { SessionId = sessionId, PayloadBytes = plain.Value };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().BeNull();
        directRepo.VerifyAll();
        sessionMgr.VerifyAll();
        mediator.VerifyNoOtherCalls();
        peerRepo.VerifyNoOtherCalls();
    }

    [Test]
    public async Task PingRequest_sends_mediator_and_returns_empty()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        var (_, plain) = MakeRatchetAndPlain(BuildEnvelope(env =>
        {
            env.DhtEnvelope = new DhtEnvelope { PingRequest = new PingRequest() };
        }).Bytes);

        sessionMgr.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(plain);

        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));

        var endpoint = new GrpcEndPoint(new DnsEndPoint("127.0.0.1", 5001), DateTimeOffset.UtcNow);
        var identityKey = new DirectMessagePublicKey(RandomBytes(32));
        peerRepo.Setup(p => p.GetByIdAsync(It.Is<Percolator.Network.PeerId>(id => id.Value == remotePeerId)))
            .ReturnsAsync(new PeerConnection(new Percolator.Network.PeerId(remotePeerId), identityKey, new[] { endpoint }, Array.Empty<TlsCertificate>(), DateTimeOffset.UtcNow));

        mediator.Setup(m => m.Send(It.IsAny<Percolator.Dht.Messages.PingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Percolator.Dht.Messages.PingResponse());

        var cmd = new DeliverOpaqueMessageCommand { SessionId = sessionId, PayloadBytes = plain.Value };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().BeNull();
        mediator.Verify(m => m.Send(It.Is<Percolator.Dht.Messages.PingRequest>(req => req.SenderEndPoint == endpoint.EndPoint), It.IsAny<CancellationToken>()), Times.Once);
        sessionMgr.VerifyAll();
        peerRepo.VerifyAll();
        directRepo.VerifyAll();
    }

    [Test]
    public async Task FindNodeRequest_returns_encrypted_response_bytes()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        var (_, plain) = MakeRatchetAndPlain(BuildEnvelope(env =>
        {
            env.DhtEnvelope = new DhtEnvelope { FindNodeRequest = new Contracts.FindNodeRequest { TargetPeerId = ByteString.CopyFrom(RandomBytes(32)) } };
        }).Bytes);

        sessionMgr.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(plain);

        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));

        var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 5001), DateTimeOffset.UtcNow);
        var identityKey = new DirectMessagePublicKey(RandomBytes(32));
        peerRepo.Setup(p => p.GetByIdAsync(It.Is<Percolator.Network.PeerId>(id => id.Value == remotePeerId)))
            .ReturnsAsync(new PeerConnection(new Percolator.Network.PeerId(remotePeerId), identityKey, new[] { endpoint }, Array.Empty<TlsCertificate>(), DateTimeOffset.UtcNow));

        mediator.Setup(m => m.Send(It.IsAny<Percolator.Dht.Messages.FindNodeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Percolator.Dht.Messages.FindNodeResponse(Array.Empty<Percolator.Dht.DhtNode>()));

        var encryptedBytes = RandomBytes(80);
        sessionMgr.Setup(s => s.EncryptMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>()))
            .ReturnsAsync(new SessionRatchetMessage(encryptedBytes));

        var cmd = new DeliverOpaqueMessageCommand { SessionId = sessionId, PayloadBytes = plain.Value };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().NotBeNull();
        result.ResponsePayloadBytes!.Should().BeEquivalentTo(encryptedBytes);

        sessionMgr.VerifyAll();
        peerRepo.VerifyAll();
        directRepo.VerifyAll();
        mediator.VerifyAll();
    }
}
