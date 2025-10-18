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
using Percolator.Network;
using Percolator.Cryptography;

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

        [Test]
        public async Task RelayOpaqueEnvelope_dispatches_to_relays_and_returns_empty()
        {
            var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);
            var sessionId = Guid.NewGuid();
            var remotePeerId = Guid.NewGuid();

            // Build a valid ratchet payload with RelayOpaqueEnvelope
            var headerKeyBytes = RandomBytes(32);
            var headerKey = new PreKey(headerKeyBytes);
            var innerOpaque = RandomBytes(24);
            var plain = new Plaintext(new InternalEnvelope
            {
                RelayOpaqueEnvelope = new RelayOpaqueEnvelope { OpaquePayload = Google.Protobuf.ByteString.CopyFrom(innerOpaque) }
            }.ToByteArray());
            var payloadBytes = BuildRatchetPayload(headerKey.Value, plain.Value);

            // Resolve and decrypt
            ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey.Value)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DirectSessionId(sessionId));
            sessionMgr.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<SessionRatchetMessage>()))
                .ReturnsAsync(plain);

            // Map session and peer
            directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
                .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
            var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 5055), DateTimeOffset.UtcNow);
            var identityKey = new DirectMessagePublicKey(RandomBytes(32));
            peerRepo.Setup(p => p.GetByIdAsync(It.Is<Percolator.Network.PeerId>(id => id.Value == remotePeerId)))
                .ReturnsAsync(new PeerConnection(new Percolator.Network.PeerId(remotePeerId), identityKey, new[] { endpoint }, Array.Empty<TlsCertificate>(), DateTimeOffset.UtcNow));
            peerRepo.Setup(p => p.SaveAsync(It.IsAny<PeerConnection>())).Returns(Task.CompletedTask);

            // Orchestrator: expect relay processor to be invoked
            mediator.Setup(m => m.Send(It.IsAny<Percolator.Application.Network.Handshake.ProcessRelayedOpaquePayloadCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Allow index upsert after decrypt
            ratchetLookup.Setup(l => l.UpsertAsync(new DirectSessionId(sessionId), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes };
            var result = await handler.Handle(cmd, CancellationToken.None);

            result.ResponsePayloadBytes.Should().BeNull();
            mediator.Verify(m => m.Send(It.IsAny<Percolator.Application.Network.Handshake.ProcessRelayedOpaquePayloadCommand>(), It.IsAny<CancellationToken>()), Times.Once);
            sessionMgr.VerifyAll();
            peerRepo.VerifyAll();
            directRepo.VerifyAll();
        }

        [Test]
        public async Task RelayOpaqueEnvelope_with_AckId_returns_encrypted_ack_response()
        {
            var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);
            var sessionId = Guid.NewGuid();
            var remotePeerId = Guid.NewGuid();

            // Header key and inner opaque
            var headerKeyBytes = RandomBytes(32);
            var headerKey = new PreKey(headerKeyBytes);
            var innerOpaque = RandomBytes(24);
            var ackId = Guid.NewGuid();

            // Build envelope with RelayOpaqueEnvelope including MessageAckId
            var plain = new Plaintext(new InternalEnvelope
            {
                RelayOpaqueEnvelope = new RelayOpaqueEnvelope
                {
                    OpaquePayload = ByteString.CopyFrom(innerOpaque),
                    MessageAckId = ByteString.CopyFrom(ackId.ToByteArray())
                }
            }.ToByteArray());
            var payloadBytes = BuildRatchetPayload(headerKey.Value, plain.Value);

            // Fast-path resolve and decrypt
            ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey.Value)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DirectSessionId(sessionId));
            sessionMgr.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<SessionRatchetMessage>()))
                .ReturnsAsync(plain);

            // Session mapping and peer info
            directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
                .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
            var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 6060), DateTimeOffset.UtcNow);
            peerRepo.Setup(p => p.GetByIdAsync(It.Is<Percolator.Network.PeerId>(id => id.Value == remotePeerId)))
                .ReturnsAsync(new PeerConnection(new Percolator.Network.PeerId(remotePeerId), new DirectMessagePublicKey(RandomBytes(32)), new[] { endpoint }, Array.Empty<TlsCertificate>(), DateTimeOffset.UtcNow));
            peerRepo.Setup(p => p.SaveAsync(It.IsAny<PeerConnection>())).Returns(Task.CompletedTask);

            // Orchestrator invoked for relay inner processing
            mediator.Setup(m => m.Send(It.IsAny<Percolator.Application.Network.Handshake.ProcessRelayedOpaquePayloadCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Upsert after decrypt
            ratchetLookup.Setup(l => l.UpsertAsync(new DirectSessionId(sessionId), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Expect encryption of RelayOpaqueResponse
            var expectedAckCipher = RandomBytes(64);
            sessionMgr.Setup(s => s.EncryptMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>()))
                .ReturnsAsync(new SessionRatchetMessage(expectedAckCipher));

            var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes };
            var result = await handler.Handle(cmd, CancellationToken.None);

            result.ResponsePayloadBytes.Should().NotBeNull();
            result.ResponsePayloadBytes!.Should().BeEquivalentTo(expectedAckCipher);

            mediator.Verify(m => m.Send(It.IsAny<Percolator.Application.Network.Handshake.ProcessRelayedOpaquePayloadCommand>(), It.IsAny<CancellationToken>()), Times.Once);
            peerRepo.VerifyAll();
            directRepo.VerifyAll();
            sessionMgr.VerifyAll();
        }

    [Test]
    public async Task MQ_Enqueue_request_results_in_early_encrypted_response()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        // Build a valid ratchet payload with an MQ Enqueue request
        var headerKey = new PreKey(RandomBytes(32));
        var mqReq = new MessageQueueEnvelope
        {
            EnqueueOpaqueMessageRequest = new EnqueueOpaqueMessageRequest
            {
                RecipientPublicKeyHash = ByteString.CopyFrom(RandomBytes(32)),
                MessageBlob = ByteString.CopyFrom(RandomBytes(24))
            }
        };

        var env = new InternalEnvelope { MessageQueueEnvelope = mqReq };
        var plain = new Plaintext(env.ToByteArray());
        var payloadBytes = BuildRatchetPayload(headerKey.Value, plain.Value);

        // Ratchet resolves and decrypt
        ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey.Value)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectSessionId(sessionId));
        sessionMgr.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(plain);

        // Session mapping and peer info
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
        var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 7777), DateTimeOffset.UtcNow);
        peerRepo.Setup(p => p.GetByIdAsync(It.Is<Percolator.Network.PeerId>(id => id.Value == remotePeerId)))
            .ReturnsAsync(new PeerConnection(new Percolator.Network.PeerId(remotePeerId), new DirectMessagePublicKey(RandomBytes(32)), new[] { endpoint }, Array.Empty<TlsCertificate>(), DateTimeOffset.UtcNow));
        peerRepo.Setup(p => p.SaveAsync(It.IsAny<PeerConnection>())).Returns(Task.CompletedTask);
        // For disallowed envelope path, handler returns before updating LastSeen/SaveAsync; no SaveAsync expected.

        // Orchestrator returns enqueue response
        var responseEnv = new InternalEnvelope { EnqueueOpaqueMessageResponse = new EnqueueOpaqueMessageResponse { Accepted = true } };
        mediator.Setup(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(responseEnv);

        // Encryption of the response
        var encrypted = RandomBytes(40);
        sessionMgr.Setup(s => s.EncryptMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>()))
            .ReturnsAsync(new SessionRatchetMessage(encrypted));

        // Upsert index
        ratchetLookup.Setup(l => l.UpsertAsync(new DirectSessionId(sessionId), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().NotBeNull();
        result.ResponsePayloadBytes!.Should().BeEquivalentTo(encrypted);

        sessionMgr.VerifyAll();
        mediator.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        peerRepo.VerifyAll();
        directRepo.VerifyAll();
    }

    [Test]
    public async Task Early_response_from_orchestrator_is_encrypted_and_returned()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        // Build a valid ratchet payload with a Chat TextMessage (or any allowed case)
        var headerKey = new PreKey(RandomBytes(32));
        var env = new InternalEnvelope { MessageQueueEnvelope = new MessageQueueEnvelope { FetchQueuedMessagesRequest = new FetchQueuedMessagesRequest { MaxCount = 5 } } };
        var plain = new Plaintext(env.ToByteArray());
        var payloadBytes = BuildRatchetPayload(headerKey.Value, plain.Value);

        // Ratchet resolves
        ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey.Value)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectSessionId(sessionId));

        // Decrypt
        sessionMgr.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(plain);

        // Direct session mapping and peer info
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
        var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 7000), DateTimeOffset.UtcNow);
        peerRepo.Setup(p => p.GetByIdAsync(It.Is<Percolator.Network.PeerId>(id => id.Value == remotePeerId)))
            .ReturnsAsync(new PeerConnection(new Percolator.Network.PeerId(remotePeerId), new DirectMessagePublicKey(RandomBytes(32)), new[] { endpoint }, Array.Empty<TlsCertificate>(), DateTimeOffset.UtcNow));
        peerRepo.Setup(p => p.SaveAsync(It.IsAny<PeerConnection>())).Returns(Task.CompletedTask);

        // Orchestrator returns a response envelope (e.g., MQ fetch response)
        var responseEnv = new InternalEnvelope { FetchQueuedMessagesResponse = new FetchQueuedMessagesResponse { } };
        mediator.Setup(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(responseEnv);

        // Encryption of the response
        var encrypted = RandomBytes(48);
        sessionMgr.Setup(s => s.EncryptMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>()))
            .ReturnsAsync(new SessionRatchetMessage(encrypted));

        // Upsert index
        ratchetLookup.Setup(l => l.UpsertAsync(new DirectSessionId(sessionId), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().NotBeNull();
        result.ResponsePayloadBytes!.Should().BeEquivalentTo(encrypted);

        sessionMgr.VerifyAll();
        mediator.VerifyAll();
        peerRepo.VerifyAll();
        directRepo.VerifyAll();
    }

    [Test]
    public async Task PlaintextHello_fallback_establishes_and_returns_encrypted_responder()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);

        // Build a minimal, valid HandshakeInitiatorHello protobuf
        using var ik = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(ik.ExportSubjectPublicKeyInfo()),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(eph.ExportSubjectPublicKeyInfo()),
            SignedPreKeyId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
        };
        var helloBytes = hello.ToByteArray();

        // Mediator now returns responder ratchet bytes directly
        var expectedCipher = RandomBytes(64);
        mediator.Setup(m => m.Send(It.IsAny<Percolator.Application.Network.Handshake.HandleHandshakeInitiatorHelloCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedCipher);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = helloBytes };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().NotBeNull();
        result.ResponsePayloadBytes!.Should().BeEquivalentTo(expectedCipher);

        mediator.Verify(m => m.Send(It.IsAny<Percolator.Application.Network.Handshake.HandleHandshakeInitiatorHelloCommand>(), It.IsAny<CancellationToken>()), Times.Once);
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
        // Common setup: orchestrator delegation returns null by default (no early response)
        mediator.Setup(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InternalEnvelope?)null);
        // Relay orchestrator: queue empty so loop is a no-op in tests
        var mqRepo = new Mock<Percolator.MessageQueue.Abstractions.IMessageQueueRepository>(MockBehavior.Strict);
        mqRepo.Setup(r => r.FetchAsync(It.IsAny<Percolator.Identity.PeerId>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(Guid, byte[])>());
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var relayLogger = Mock.Of<ILogger<RelayOrchestrator>>();
        var relay = new RelayOrchestrator(relayLogger, mqRepo.Object, directRepo.Object, sessionMgr.Object, transport.Object, active);
        return new DeliverOpaqueMessageHandler(logger, sessionMgr.Object, peerRepo.Object, mediator.Object, directRepo.Object, active, ratchetLookup.Object, relay);
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

        // Orchestrator-centric: handler delegates to ProcessInternalEnvelopeCommand which returns a response envelope
        var orchestratorResp = new InternalEnvelope
        {
            DhtEnvelope = new DhtEnvelope
            {
                FindNodeResponse = new Contracts.FindNodeResponse()
            }
        };
        mediator.Setup(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(orchestratorResp);

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

    [Test]
    public async Task Disallowed_empty_envelope_returns_empty_and_does_not_dispatch()
    {
        var handler = CreateHandler(out var sessionMgr, out var peerRepo, out var mediator, out var directRepo, out var ratchetLookup);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        // Build an InternalEnvelope with no payload set (ApplicationPayloadCase == None)
        var emptyEnv = new InternalEnvelope();
        var plain = new Plaintext(emptyEnv.ToByteArray());

        // Header and payload
        var headerKey = new PreKey(RandomBytes(32));
        var payloadBytes = BuildRatchetPayload(headerKey.Value, plain.Value);

        // Ratchet resolves and decrypt succeeds
        ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey.Value)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectSessionId(sessionId));
        sessionMgr.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(plain);

        // Direct session mapping and peer info are looked up before prefilter, set them up
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
        var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 5050), DateTimeOffset.UtcNow);
        peerRepo.Setup(p => p.GetByIdAsync(It.Is<Percolator.Network.PeerId>(id => id.Value == remotePeerId)))
            .ReturnsAsync(new PeerConnection(new Percolator.Network.PeerId(remotePeerId), new DirectMessagePublicKey(RandomBytes(32)), new[] { endpoint }, Array.Empty<TlsCertificate>(), DateTimeOffset.UtcNow));
        peerRepo.Setup(p => p.SaveAsync(It.IsAny<PeerConnection>())).Returns(Task.CompletedTask);

        // Upsert after decrypt is allowed
        ratchetLookup.Setup(l => l.UpsertAsync(new DirectSessionId(sessionId), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().BeNull();
        // Ensure orchestrator was NOT called due to prefilter rejection
        mediator.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Never);

        sessionMgr.VerifyAll();
        peerRepo.Verify(p => p.SaveAsync(It.IsAny<PeerConnection>()), Times.Never);
        directRepo.VerifyAll();
    }
}
