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
using Percolator.Application.KeyExchange;
using Percolator.Identity;
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

    [Test]
    public async Task SlowPath_succeeds_when_fast_lookup_misses()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);
        var sessionId = Guid.NewGuid();

        // Build a valid ratchet payload with a DHT PingRequest envelope
        var headerKey = new PreKey(RandomBytes(32));
        var pingEnvelope = new Percolator.Contracts.InternalEnvelope
        {
            DhtEnvelope = new Percolator.Contracts.DhtEnvelope { PingRequest = new Percolator.Contracts.PingRequest() }
        };
        var plaintext = new Plaintext(pingEnvelope.ToByteArray());
        var payloadBytes = BuildRatchetPayload(headerKey.Value, plaintext.Value);

        // Fast path miss
        ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey.Value)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DirectSessionId?)null);

        // Slow path hit
        sessionMgr.Setup(s => s.TryInferAndReceiveAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), plaintext));

        // Direct session mapping and peer info
        var remotePeerId = Guid.NewGuid();
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
        var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("localhost", 6000), DateTimeOffset.UtcNow);
        peerRepo.Setup(p => p.GetByIdAsync(It.IsAny<Percolator.Network.PeerId>()))
            .ReturnsAsync(new PeerConnection(new Percolator.Network.PeerId(remotePeerId), new DirectMessagePublicKey(RandomBytes(32)), new[] { endpoint }, Array.Empty<TlsCertificate>(), DateTimeOffset.UtcNow));
        peerRepo.Setup(p => p.SaveAsync(It.IsAny<PeerConnection>())).Returns(Task.CompletedTask);

        // Mediator responds to Ping
        mediator.Setup(m => m.Send(It.IsAny<Percolator.Dht.Messages.PingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Percolator.Dht.Messages.PingResponse());

        // Allow index upsert in handler
        ratchetLookup.Setup(l => l.UpsertAsync(new DirectSessionId(sessionId), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().BeNull();
        mediator.Verify(m => m.Send(It.IsAny<Percolator.Dht.Messages.PingRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Throws_when_both_fast_and_slow_paths_fail()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);
        var headerKey = new PreKey(RandomBytes(32));
        var payloadBytes = BuildRatchetPayload(headerKey.Value, RandomBytes(16));

        // Fast path miss
        ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey.Value)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DirectSessionId?)null);
            
        // Slow path miss
        sessionMgr.Setup(s => s.TryInferAndReceiveAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((SessionId, Plaintext)?)null);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes };
        Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(cmd, CancellationToken.None));
    }

    private static byte[] BuildRatchetPayload(byte[] headerKeyBytes, byte[] ciphertext)
    {
        var ratchet = new RatchetMessage
        {
            Header = new RatchetHeader
            {
                RatchetKey = ByteString.CopyFrom(headerKeyBytes),
                Counter = 0,
                PreviousChainLength = 0
            },
            Ciphertext = ByteString.CopyFrom(ciphertext)
        };
        return ratchet.ToByteArray();
    }

    private static DeliverOpaqueMessageHandler CreateHandler(
        out Mock<IDirectSessionManager> sessionMgr,
        out Mock<IPeerConnectionRepository> peerRepo,
        out Mock<IMediator> mediator,
        out Mock<IDirectSessionRepository> directRepo,
        out Mock<IRatchetKeySessionLookup> ratchetLookup)
    {
        sessionMgr = new Mock<IDirectSessionManager>(MockBehavior.Strict);
        peerRepo = new Mock<IPeerConnectionRepository>(MockBehavior.Strict);
        mediator = new Mock<IMediator>(MockBehavior.Strict);
        directRepo = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        ratchetLookup = new Mock<IRatchetKeySessionLookup>(MockBehavior.Strict);
        var logger = Mock.Of<ILogger<DeliverOpaqueMessageHandler>>();
        var active = new ActiveIdentityContext
        {
            Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Test", null) { SelfIdentityId = 1 }
        };
        return new DeliverOpaqueMessageHandler(logger, sessionMgr.Object, peerRepo.Object, mediator.Object, directRepo.Object, active, ratchetLookup.Object);
    }

    [Test]
    public async Task Returns_empty_when_decryption_result_is_null()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);
        var sessionId = Guid.NewGuid();

        // Arrange ratchet lookup to resolve inferred session from header key
        var headerKey = new PreKey(RandomBytes(32));
        ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey.Value)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectSessionId(sessionId));
        // Allow index upsert even if we return early on null plaintext (handler upserts after successful decrypt only; this is defensive)
        ratchetLookup.Setup(l => l.UpsertAsync(new DirectSessionId(sessionId), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Decryption yields null -> handler returns empty result before mapping/peer lookups
        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = BuildRatchetPayload(headerKey.Value, RandomBytes(48)) };
        sessionMgr.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync((Plaintext?)null);

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().BeNull();
        sessionMgr.VerifyAll();
    }

    [Test]
    public void Throws_when_direct_session_mapping_missing()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);
        var sessionId = Guid.NewGuid();

        // Build a valid ratchet payload and resolve session via ratchet lookup
        var headerKey = new PreKey(RandomBytes(32));
        ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey.Value)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectSessionId(sessionId));

        // Cause direct session mapping lookup to fail so handler throws
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync((DirectSession?)null);

        // Ensure we proceed past decrypt step to hit the mapping check
        var plain = new Plaintext(RandomBytes(12));
        sessionMgr.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(plain);

        // Allow index upsert after decrypt
        ratchetLookup.Setup(l => l.UpsertAsync(new DirectSessionId(sessionId), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = BuildRatchetPayload(headerKey.Value, plain.Value) };
        Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(cmd, CancellationToken.None));

        directRepo.VerifyAll();
    }

    [Test]
    public async Task PingRequest_sends_mediator_and_returns_empty()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        var headerKeyBytes = RandomBytes(32);
        var headerKey = new PreKey(headerKeyBytes);
        var plain = new Plaintext(BuildEnvelope(env =>
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
        
        // Handler updates LastSeen and persists the connection
        peerRepo.Setup(p => p.SaveAsync(It.IsAny<PeerConnection>())).Returns(Task.CompletedTask);

        mediator.Setup(m => m.Send(It.IsAny<Percolator.Dht.Messages.PingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Percolator.Dht.Messages.PingResponse());

        // Ratchet lookup resolves inferred session id
        ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey.Value)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectSessionId(sessionId));

        // Allow index upsert after decrypt
        ratchetLookup.Setup(l => l.UpsertAsync(new DirectSessionId(sessionId), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = BuildRatchetPayload(headerKey.Value, plain.Value) };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().BeNull();
        mediator.Verify(m => m.Send(It.Is<Percolator.Dht.Messages.PingRequest>(req => req.SenderEndPoint == endpoint.EndPoint), It.IsAny<CancellationToken>()), Times.Once);
        sessionMgr.VerifyAll();
        peerRepo.VerifyAll();
        // Updated handler updates LastSeen and saves connection info
        peerRepo.Verify(p => p.SaveAsync(It.IsAny<PeerConnection>()), Times.Once);
        directRepo.VerifyAll();
    }

    [Test]
    public async Task FindNodeRequest_returns_encrypted_response_bytes()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        var headerKeyBytes = RandomBytes(32);
        var headerKey = new PreKey(headerKeyBytes);
        var plain = new Plaintext(BuildEnvelope(env =>
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
        
        // Handler updates LastSeen and persists the connection
        peerRepo.Setup(p => p.SaveAsync(It.IsAny<PeerConnection>())).Returns(Task.CompletedTask);

        mediator.Setup(m => m.Send(It.IsAny<Percolator.Dht.Messages.FindNodeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Percolator.Dht.Messages.FindNodeResponse(Array.Empty<Percolator.Dht.DhtNode>()));

        // Ratchet lookup resolves inferred session id
        ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey.Value)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectSessionId(sessionId));

        // Allow index upsert after encrypt path
        ratchetLookup.Setup(l => l.UpsertAsync(new DirectSessionId(sessionId), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var encryptedBytes = RandomBytes(80);
        sessionMgr.Setup(s => s.EncryptMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>()))
            .ReturnsAsync(new SessionRatchetMessage(encryptedBytes));

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = BuildRatchetPayload(headerKey.Value, plain.Value) };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().NotBeNull();
        result.ResponsePayloadBytes!.Should().BeEquivalentTo(encryptedBytes);

        sessionMgr.VerifyAll();
        peerRepo.VerifyAll();
        // Updated handler updates LastSeen and saves connection info
        peerRepo.Verify(p => p.SaveAsync(It.IsAny<PeerConnection>()), Times.Once);
        directRepo.VerifyAll();
        mediator.VerifyAll();
    }
}
