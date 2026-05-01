using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Application.Network;
using Percolator.Application.Services;
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
        public async Task RelayOpaqueEnvelope_passes_remote_peer_as_RelayHostPeerId()
        {
            var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
            var sessionId = Guid.NewGuid();
            var remotePeerId = Guid.NewGuid();

            // Build a valid ratchet payload with RelayOpaqueEnvelope
            var headerKeyBytes = RandomBytes(64);
            var headerKey = PreKey.FromBytes(headerKeyBytes);
            var innerOpaque = RandomBytes(24);
            var plain = Plaintext.FromBytes(new InternalEnvelope
            {
                RelayOpaqueEnvelope = new RelayOpaqueEnvelope { OpaquePayload = Google.Protobuf.ByteString.CopyFrom(innerOpaque) }
            }.ToByteArray());
            var payloadBytes = BuildRatchetPayload(headerKey.ToArray(), plain.ToArray());

            // Resolve and decrypt
            ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SessionId(sessionId));
            secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((new SessionId(sessionId), plain));

            // Map session and peer
            directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
                .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
            var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 5056), DateTimeOffset.UtcNow);
            var identityKey = DirectMessagePublicKey.FromBytes(RandomBytes(80));

            // Capture RelayHostPeerId passed to mediator
            Percolator.Identity.PeerId? capturedRelayHost = null;
            mediator.Setup(m => m.Send(It.IsAny<Percolator.Application.Network.Handshake.ProcessRelayedOpaquePayloadCommand>(), It.IsAny<CancellationToken>()))
                .Callback<object, CancellationToken>((cmd, _) =>
                {
                    if (cmd is Percolator.Application.Network.Handshake.ProcessRelayedOpaquePayloadCommand c)
                    {
                        capturedRelayHost = c.RelayHostPeerId;
                    }
                })
                .ReturnsAsync(Percolator.Application.Network.Handshake.ProcessRelayedOpaquePayloadResponse.Success);

            // Allow ratchet index upsert after decrypt in handler
            ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<int>(), It.Is<SessionId>(s => s.Value == sessionId), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // RPC-level ack encryption via SecureMessagingService
            secureSvc.Setup(s => s.EncryptAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(SessionRatchetMessage.FromBytes(RandomBytes(20)));

            var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes, SelfIdentityId = new SelfId(1) };
            _ = await handler.Handle(cmd, CancellationToken.None);

            Assert.That(capturedRelayHost, Is.Not.Null);
            Assert.That(capturedRelayHost, Is.EqualTo(new Percolator.Identity.PeerId(remotePeerId)));
        }

        [Test]
        public async Task RelayOpaqueEnvelope_dispatches_to_relays_and_returns_empty()
        {
            var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
            var sessionId = Guid.NewGuid();
            var remotePeerId = Guid.NewGuid();

            // Build a valid ratchet payload with RelayOpaqueEnvelope
            var headerKeyBytes = RandomBytes(64);
            var headerKey = PreKey.FromBytes(headerKeyBytes);
            var innerOpaque = RandomBytes(24);
            var plain = Plaintext.FromBytes(new InternalEnvelope
            {
                RelayOpaqueEnvelope = new RelayOpaqueEnvelope { OpaquePayload = Google.Protobuf.ByteString.CopyFrom(innerOpaque) }
            }.ToByteArray());
            var payloadBytes = BuildRatchetPayload(headerKey.ToArray(), plain.ToArray());

            // Resolve and decrypt
            ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SessionId(sessionId));
            secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((new SessionId(sessionId), plain));

            // Map session and peer
            directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
                .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
            var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 5055), DateTimeOffset.UtcNow);
            var identityKey = DirectMessagePublicKey.FromBytes(RandomBytes(80));

            // Orchestrator: envelope is delegated to ProcessInternalEnvelopeCommand
            mediator.Setup(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((InternalEnvelope?)null);

            // Allow index upsert after decrypt
            ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<int>(), It.Is<SessionId>(s => s.Value == sessionId), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // RPC-level ack encryption via SecureMessagingService
            var ackCipher = RandomBytes(32);
            secureSvc.Setup(s => s.EncryptAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(SessionRatchetMessage.FromBytes(ackCipher));

            var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes, SelfIdentityId = new SelfId(1) };
            var result = await handler.Handle(cmd, CancellationToken.None);

            // Expect RPC-level ack payload (encrypted) for RelayOpaqueEnvelope path
            result.ResponsePayloadBytes.Should().NotBeNull();
            mediator.Verify(m => m.Send(It.IsAny<Percolator.Application.Network.Handshake.ProcessRelayedOpaquePayloadCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task RelayOpaqueEnvelope_with_AckId_is_delegated_and_returns_empty()
        {
            var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
            var sessionId = Guid.NewGuid();
            var remotePeerId = Guid.NewGuid();

            // Header key and inner opaque
            var headerKeyBytes = RandomBytes(64);
            var headerKey = PreKey.FromBytes(headerKeyBytes);
            var innerOpaque = RandomBytes(24);
            var ackId = Guid.NewGuid();

            // Build envelope with RelayOpaqueEnvelope including MessageAckId
            var plain = Plaintext.FromBytes(new InternalEnvelope
            {
                RelayOpaqueEnvelope = new RelayOpaqueEnvelope
                {
                    OpaquePayload = ByteString.CopyFrom(innerOpaque),
                    MessageAckId = ByteString.CopyFrom(ackId.ToByteArray())
                }
            }.ToByteArray());
            var payloadBytes = BuildRatchetPayload(headerKey.ToArray(), plain.ToArray());

            // Fast-path resolve and decrypt
            ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SessionId(sessionId));
            secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((new SessionId(sessionId), plain));

            // Session mapping and peer info
            directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
                .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
            var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 6060), DateTimeOffset.UtcNow);

            // Orchestrator invoked via ProcessInternalEnvelopeCommand (no early response expected)
            mediator.Setup(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((InternalEnvelope?)null);

            // Upsert after decrypt
            ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<int>(), It.Is<SessionId>(s => s.Value == sessionId), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // RPC-level ack encryption via SecureMessagingService
            var ackCipher2 = RandomBytes(28);
            secureSvc.Setup(s => s.EncryptAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(SessionRatchetMessage.FromBytes(ackCipher2));

            var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes, SelfIdentityId = new SelfId(1) };
            var result = await handler.Handle(cmd, CancellationToken.None);

            // Expect RPC-level ack payload (encrypted) when MessageAckId is present
            result.ResponsePayloadBytes.Should().NotBeNull();

            mediator.Verify(m => m.Send(It.IsAny<Percolator.Application.Network.Handshake.ProcessRelayedOpaquePayloadCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        }

    [Test]
    public async Task MQ_Enqueue_request_results_in_early_encrypted_response()
    {
        var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        // Build a valid ratchet payload with an MQ Enqueue request
        var headerKey = PreKey.FromBytes(RandomBytes(64));
        var mqReq = new MessageQueueEnvelope
        {
            EnqueueOpaqueMessageRequest = new EnqueueOpaqueMessageRequest
            {
                RecipientPublicKeyHash = ByteString.CopyFrom(RandomBytes(32)),
                MessageBlob = ByteString.CopyFrom(RandomBytes(24))
            }
        };

        var env = new InternalEnvelope { MessageQueueEnvelope = mqReq };
        var plain = Plaintext.FromBytes(env.ToByteArray());
        var payloadBytes = BuildRatchetPayload(headerKey.ToArray(), plain.ToArray());

        // Ratchet resolves and decrypt
        ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionId(sessionId));
        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), plain));

        // Session mapping and peer info
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
        var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 7777), DateTimeOffset.UtcNow);
        // For disallowed envelope path, handler returns before updating LastSeen/SaveAsync; no SaveAsync expected.

        // Orchestrator returns enqueue response
        var responseEnv = new InternalEnvelope { EnqueueOpaqueMessageResponse = new EnqueueOpaqueMessageResponse { Accepted = true } };
        mediator.Setup(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(responseEnv);

        // Encryption of the response via SecureMessagingService
        var encrypted = RandomBytes(40);
        secureSvc.Setup(s => s.EncryptAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(encrypted));

        // Upsert index
        ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<int>(), It.Is<SessionId>(s => s.Value == sessionId), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes, SelfIdentityId = new SelfId(1) };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().NotBeNull();
        result.ResponsePayloadBytes!.Should().BeEquivalentTo(encrypted);

        mediator.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Early_response_from_orchestrator_is_encrypted_and_returned()
    {
        var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        // Build a valid ratchet payload with a Chat TextMessage (or any allowed case)
        var headerKey = PreKey.FromBytes(RandomBytes(64));
        var env = new InternalEnvelope { MessageQueueEnvelope = new MessageQueueEnvelope { FetchQueuedMessagesRequest = new FetchQueuedMessagesRequest { MaxCount = 5 } } };
        var plain = Plaintext.FromBytes(env.ToByteArray());
        var payloadBytes = BuildRatchetPayload(headerKey.ToArray(), plain.ToArray());

        // Ratchet resolves
        ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionId(sessionId));

        // Decrypt
        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), plain));

        // Direct session mapping and peer info
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
        var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 7000), DateTimeOffset.UtcNow);

        // Orchestrator returns a response envelope (e.g., MQ fetch response)
        var responseEnv = new InternalEnvelope { FetchQueuedMessagesResponse = new FetchQueuedMessagesResponse { } };
        mediator.Setup(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(responseEnv);

        // Encryption of the response via SecureMessagingService
        var encrypted = RandomBytes(48);
        secureSvc.Setup(s => s.EncryptAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(encrypted));

        // Upsert index
        ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<int>(), It.Is<SessionId>(s => s.Value == sessionId), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes, SelfIdentityId = new SelfId(1) };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().NotBeNull();
        result.ResponsePayloadBytes!.Should().BeEquivalentTo(encrypted);

        mediator.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task PlaintextHello_fallback_establishes_and_returns_encrypted_responder()
    {
        var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);

        // Build a minimal, valid HandshakeInitiatorHello protobuf carried within RelayOpaqueEnvelope (as inner opaque)
        using var ik = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(ik.ExportSubjectPublicKeyInfo()),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(eph.ExportSubjectPublicKeyInfo()),
            SignedPreKeyId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
        };
        var innerEnv = new InternalEnvelope { RelayOpaqueEnvelope = new RelayOpaqueEnvelope { OpaquePayload = ByteString.CopyFrom(hello.ToByteArray()) } };
        var ratchetHeaderKey = PreKey.FromBytes(RandomBytes(64));
        var ratchetPayload = BuildRatchetPayload(ratchetHeaderKey.ToArray(), innerEnv.ToByteArray());

        // Resolve and decrypt the outer ratchet payload for this test path
        var sessionId = Guid.NewGuid();
        ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(ratchetHeaderKey.ToArray())), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionId(sessionId));
        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), Plaintext.FromBytes(innerEnv.ToByteArray())));
        // Map session to a remote peer and provide connection info
        var remotePeerGuid = Guid.NewGuid();
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerGuid), new DirectSessionId(sessionId)));
        var endpoint3 = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 5050), DateTimeOffset.UtcNow);
        // peer connection lookup removed in new design
        ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<int>(), It.Is<SessionId>(s => s.Value == sessionId), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // RPC-level ack encryption via SecureMessagingService
        var ackCipher3 = RandomBytes(36);
        secureSvc.Setup(s => s.EncryptAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(ackCipher3));

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = ratchetPayload, SelfIdentityId = new SelfId(1) };
        var result = await handler.Handle(cmd, CancellationToken.None);

        // The RPC response is an ack; only assert non-null and that relayed payload was processed
        result.ResponsePayloadBytes.Should().NotBeNull();
        mediator.Verify(m => m.Send(It.IsAny<Percolator.Application.Network.Handshake.ProcessRelayedOpaquePayloadCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static byte[] RandomBytes(int len = 32) => RandomNumberGenerator.GetBytes(len);

    [Test]
    public async Task SlowPath_succeeds_when_fast_lookup_misses()
    {
        var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
        var sessionId = Guid.NewGuid();

        // Build a valid ratchet payload with a DHT PingRequest envelope
        var headerKey = PreKey.FromBytes(RandomBytes(64));
        var pingEnvelope = new Percolator.Contracts.InternalEnvelope
        {
            DhtEnvelope = new Percolator.Contracts.DhtEnvelope { PingRequest = new Percolator.Contracts.PingRequest() }
        };
        var plaintext = Plaintext.FromBytes(pingEnvelope.ToByteArray());
        var payloadBytes = BuildRatchetPayload(headerKey.ToArray(), plaintext.ToArray());

        // Fast path miss
        ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId?)null);

        // Slow path hit is now handled by SecureMessagingService.DecryptInboundAsync
        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), plaintext));

        // Direct session mapping and peer info
        var remotePeerId = Guid.NewGuid();
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));
        var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("localhost", 6000), DateTimeOffset.UtcNow);

        // Orchestrator receives Ping via ProcessInternalEnvelopeCommand
        mediator.Setup(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InternalEnvelope?)null);

        // Allow index upsert in handler
        ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<int>(), It.Is<SessionId>(s => s.Value == sessionId), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes, SelfIdentityId = new SelfId(1) };
        var result = await handler.Handle(cmd, CancellationToken.None);

        // For slow-path with no orchestrator response, expect null
        result.ResponsePayloadBytes.Should().BeNull();
        mediator.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Throws_when_both_fast_and_slow_paths_fail()
    {
        var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
        var headerKey = PreKey.FromBytes(RandomBytes(64));
        var payloadBytes = BuildRatchetPayload(headerKey.ToArray(), RandomBytes(16));

        // Fast path miss
        ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId?)null);

        // Slow path miss is represented by SecureMessagingService failing to decrypt
        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException());

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes, SelfIdentityId = new SelfId(1) };
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
        out Mock<IMediator> mediator,
        out Mock<IDirectSessionRepository> directRepo,
        out Mock<IRatchetKeyIndex> ratchetLookup,
        out Mock<ISecureMessagingService> secureSvc)
    {
        mediator = new Mock<IMediator>(MockBehavior.Loose);
        directRepo = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        ratchetLookup = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
        secureSvc = new Mock<ISecureMessagingService>(MockBehavior.Strict);
        var logger = Mock.Of<ILogger<DeliverOpaqueMessageHandler>>();
        var active = new ActiveIdentityContext
        {
            Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Test", null) { SelfIdentityId = new SelfId(1) }
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
        var relay = new RelayOrchestrator(relayLogger, mqRepo.Object, directRepo.Object, secureSvc.Object, transport.Object);
        var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
        // Always return a profile with a fresh localhost endpoint for any peer id
        profileRepo
            .Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<CancellationToken>()))
            .Returns<Percolator.Network.PeerId, CancellationToken>((pid, ct) =>
            {
                var now = DateTimeOffset.UtcNow;
                var profile = new PeerRoutingProfile();
                profile.BindIdentity(pid);
                profile.AddGrpcEndPoint(new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 5001), now), now);
                return Task.FromResult<PeerRoutingProfile?>(profile);
            });

        var routePlanner = new Mock<IProfileRoutePlanner>(MockBehavior.Loose);
        routePlanner
            .Setup(p => p.SelectRoute(It.IsAny<PeerRoutingProfile>()))
            .Returns<PeerRoutingProfile>(p => new Percolator.Network.RouteSelection(p.Endpoints.First(), null));

        return new DeliverOpaqueMessageHandler(logger, mediator.Object, directRepo.Object, ratchetLookup.Object, relay, profileRepo.Object, routePlanner.Object, secureSvc.Object);
    }

    [Test]
    public async Task Returns_empty_when_decryption_result_is_null()
    {
        var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
        var sessionId = Guid.NewGuid();

        // Arrange ratchet lookup to resolve inferred session from header key
        var headerKey = PreKey.FromBytes(RandomBytes(64));
        ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionId(sessionId));

        // Decryption yields null -> handler returns empty result before mapping/peer lookups
        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = BuildRatchetPayload(headerKey.ToArray(), RandomBytes(48)), SelfIdentityId = new SelfId(1) };
        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((SessionId, Plaintext)?)null);

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().BeNull();
        // No orchestrator dispatch should occur when decrypt returns null
        mediator.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        mediator.Verify(m => m.Send(It.IsAny<Percolator.Application.Network.Handshake.ProcessRelayedOpaquePayloadCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        // No index upsert when decrypt fails
        ratchetLookup.Verify(l => l.UpsertAsync(It.IsAny<int>(), It.IsAny<SessionId>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        // No direct session mapping lookups should occur
        directRepo.VerifyNoOtherCalls();
    }

    [Test]
    public void Throws_when_direct_session_mapping_missing()
    {
        var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
        var sessionId = Guid.NewGuid();

        // Build a valid ratchet payload and resolve session via ratchet lookup
        var headerKey = PreKey.FromBytes(RandomBytes(64));
        ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionId(sessionId));

        // Cause direct session mapping lookup to fail so handler throws
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync((DirectSession?)null);

        // Ensure we proceed past decrypt step to hit the mapping check
        var plain = Plaintext.FromBytes(RandomBytes(12));
        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), plain));

        // Allow index upsert after decrypt
        ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<int>(), It.Is<SessionId>(s => s.Value == sessionId), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = BuildRatchetPayload(headerKey.ToArray(), plain.ToArray()), SelfIdentityId = new SelfId(1) };
        Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(cmd, CancellationToken.None));
    }

    [Test]
    public async Task PingRequest_sends_mediator_and_returns_empty()
    {
        var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        var headerKeyBytes = RandomBytes(64);
        var headerKey = PreKey.FromBytes(headerKeyBytes);
        var plain = Plaintext.FromBytes(BuildEnvelope(env =>
        {
            env.DhtEnvelope = new DhtEnvelope { PingRequest = new PingRequest() };
        }).Bytes);

        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), plain));

        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));

        var endpoint = new GrpcEndPoint(new DnsEndPoint("127.0.0.1", 5001), DateTimeOffset.UtcNow);
        var identityKey = DirectMessagePublicKey.FromBytes(RandomBytes(80));
        // peer connection lookup removed in new design

        mediator.Setup(m => m.Send(It.IsAny<Percolator.Dht.Messages.PingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Percolator.Dht.Messages.PingResponse());

        // Ratchet lookup resolves inferred session id
        ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionId(sessionId));

        // Allow index upsert after decrypt
        ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<int>(), It.Is<SessionId>(s => s.Value == sessionId), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = BuildRatchetPayload(headerKey.ToArray(), plain.ToArray()), SelfIdentityId = new SelfId(1) };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().BeNull();
        mediator.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task FindNodeRequest_returns_encrypted_response_bytes()
    {
        var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        var headerKeyBytes = RandomBytes(64);
        var headerKey = PreKey.FromBytes(headerKeyBytes);
        var plain = Plaintext.FromBytes(BuildEnvelope(env =>
        {
            env.DhtEnvelope = new DhtEnvelope { FindNodeRequest = new Contracts.FindNodeRequest { TargetPeerId = ByteString.CopyFrom(RandomBytes(32)) } };
        }).Bytes);

        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), plain));

        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));

        var endpoint = new GrpcEndPoint(new System.Net.DnsEndPoint("127.0.0.1", 5001), DateTimeOffset.UtcNow);
        var identityKey = DirectMessagePublicKey.FromBytes(RandomBytes(80));
        // peer connection lookup removed in new design

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
        ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionId(sessionId));

        // Allow index upsert after encrypt path
        ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<int>(), It.Is<SessionId>(s => s.Value == sessionId), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var encryptedBytes = RandomBytes(80);
        secureSvc.Setup(s => s.EncryptAsync(It.Is<SessionId>(x => x.Value == sessionId), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(encryptedBytes));

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = BuildRatchetPayload(headerKey.ToArray(), plain.ToArray()), SelfIdentityId = new SelfId(1) };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().NotBeNull();
        result.ResponsePayloadBytes!.Should().BeEquivalentTo(encryptedBytes);

        mediator.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Disallowed_empty_envelope_returns_empty_and_does_not_dispatch()
    {
        var handler = CreateHandler(out var mediator, out var directRepo, out var ratchetLookup, out var secureSvc);
        var sessionId = Guid.NewGuid();
        var remotePeerId = Guid.NewGuid();

        // Build an InternalEnvelope with ChatEnvelope to avoid empty plaintext
        var emptyEnv = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { TextMessage = new TextMessage { Content = "test" } } };
        var plain = Plaintext.FromBytes(emptyEnv.ToByteArray());

        // Header and payload
        var headerKey = PreKey.FromBytes(RandomBytes(64));
        var payloadBytes = BuildRatchetPayload(headerKey.ToArray(), plain.ToArray());

        // Ratchet resolves and decrypt succeeds
        ratchetLookup.Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.Is<RatchetEphemeralKey>(p => p.ToArray().SequenceEqual(headerKey.ToArray())), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionId(sessionId));
        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), plain));

        // Direct session mapping is looked up before prefilter, set it up
        directRepo.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(remotePeerId), new DirectSessionId(sessionId)));

        // Upsert after decrypt is allowed
        ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<int>(), It.Is<SessionId>(s => s.Value == sessionId), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Set up mediator to return a response (envelope now has content so it will be dispatched)
        mediator.Setup(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InternalEnvelope?)null);

        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payloadBytes, SelfIdentityId = new SelfId(1) };
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.ResponsePayloadBytes.Should().BeNull();
        // Ensure orchestrator was called due to envelope having content
        mediator.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
