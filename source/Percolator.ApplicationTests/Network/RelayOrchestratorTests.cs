using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Chat;
using Percolator.Application.Chat.MessageQueue;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class RelayOrchestratorTests
{
    private static (RelayOrchestrator orchestrator,
        Mock<IMessageQueueRepository> queue,
        Mock<IDirectSessionRepository> directSessions,
        Mock<IMessageTransportService> transport,
        Mock<ISecureMessagingService> secureSvc) Create(out ActiveIdentityContext active)
    {
        var logger = Mock.Of<ILogger<RelayOrchestrator>>();
        var queue = new Mock<IMessageQueueRepository>(MockBehavior.Loose);
        var directSessions = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var secureSvc = new Mock<ISecureMessagingService>(MockBehavior.Loose);
        active = new ActiveIdentityContext
        {
            Identity = new Percolator.Identity.Model.IdentityRecord(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new DeviceId(1), "Test")
        };
        var peerIdentityQueries = new Mock<IPeerIdentityQueries>(MockBehavior.Loose);
        var orchestrator = new RelayOrchestrator(logger, queue.Object, directSessions.Object, secureSvc.Object, transport.Object, peerIdentityQueries.Object);
        return (orchestrator, queue, directSessions, transport, secureSvc);
    }

    [Test]
    public async Task RelayNextAsync_happy_ack_deletes_and_returns_true()
    {
        var (orchestrator, queue, directSessions,  transport, secureSvc) = Create(out var active);
        var peerId = new Percolator.Identity.PeerId((uint)Random.Shared.Next(1, 1000000));
        var selfId = active.Identity!.SelfIdentityId;
        var ackId = Guid.NewGuid();
        var blob = new byte[] { 0x01, 0x02 };
        var sessionGuid = Guid.NewGuid();
        var sessionId = new SessionId(sessionGuid);
        var directSessionId = new DirectSessionId(sessionGuid);
        var networkPeerId = new NetworkPeerId(peerId.Value);
        var publicIdentityId = new Percolator.Identity.PublicIdentityId(Guid.NewGuid());

        var peerIdentityQueries = new Mock<IPeerIdentityQueries>(MockBehavior.Strict);
        peerIdentityQueries.Setup(q => q.GetPublicIdentityIdAsync(peerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(publicIdentityId);

        queue.Setup(q => q.FetchAsync(publicIdentityId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.List<(Guid, QueuedPayloadBytes)> { (ackId, QueuedPayloadBytes.FromBytesOwned(blob)) });
        directSessions.Setup(d => d.GetByRemotePeerIdAsync(networkPeerId, new NetworkSelfId(active.Identity!.SelfIdentityId.Value)))
            .ReturnsAsync(new DirectSession(networkPeerId, directSessionId));
        secureSvc.Setup(s => s.EncryptAsync(sessionId, It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(new byte[] { 0xAA }));

        // Transport returns a DR ciphertext, which will be passed to ReceiveMessageAsync
        var ackCipherBytes = new byte[] { 0xBB, 0xCC };
        var response = new DeliverOpaqueMessageResponse
        {
            ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
            {
                ResponsePayload = ByteString.CopyFrom(ackCipherBytes)
            }
        };
        transport.Setup(t => t.SendMessageAsync(peerId, directSessionId, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse { OriginalResponse = response });

        // DR decrypt of ack payload via SecureMessagingService yields RelayOpaqueResponse with same ack id
        var ack = new RelayOpaqueResponse { Version = 1, MessageAckId = ByteString.CopyFrom(ackId.ToByteArray()) };
        secureSvc.Setup(s => s.DecryptInboundAsync(new CryptoSelfId(selfId.Value), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sessionId, Plaintext.FromBytes(ack.ToByteArray())));

        queue.Setup(q => q.DeleteByAckIdAsync(ackId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var orchestratorWithQueries = new RelayOrchestrator(
            Mock.Of<ILogger<RelayOrchestrator>>(),
            queue.Object,
            directSessions.Object,
            secureSvc.Object,
            transport.Object,
            peerIdentityQueries.Object);

        var result = await orchestratorWithQueries.RelayNextAsync(selfId, peerId, CancellationToken.None);
        result.Should().BeTrue();
    }

    [Test]
    public void RelayNextAsync_ack_mismatch_throws()
    {
        var (orchestrator, queue, directSessions,  transport, secureSvc) = Create(out var active);
        var peerId = new Percolator.Identity.PeerId((uint)Random.Shared.Next(1, 1000000));
        var selfId = active.Identity!.SelfIdentityId;
        var ackId = Guid.NewGuid();
        var blob = new byte[] { 0x05 };
        var sessionGuid = Guid.NewGuid();
        var sessionId = new SessionId(sessionGuid);
        var directSessionId = new DirectSessionId(sessionGuid);
        var networkPeerId = new NetworkPeerId(peerId.Value);
        var publicIdentityId = new Percolator.Identity.PublicIdentityId(Guid.NewGuid());

        var peerIdentityQueries = new Mock<IPeerIdentityQueries>(MockBehavior.Strict);
        peerIdentityQueries.Setup(q => q.GetPublicIdentityIdAsync(peerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(publicIdentityId);

        queue.Setup(q => q.FetchAsync(publicIdentityId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.List<(Guid, QueuedPayloadBytes)> { (ackId, QueuedPayloadBytes.FromBytesOwned(blob)) });
        directSessions.Setup(d => d.GetByRemotePeerIdAsync(networkPeerId, new NetworkSelfId(active.Identity!.SelfIdentityId.Value)))
            .ReturnsAsync(new DirectSession(networkPeerId, directSessionId));
        secureSvc.Setup(s => s.EncryptAsync(sessionId, It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(new byte[] { 0xAA }));

        var response = new DeliverOpaqueMessageResponse
        {
            ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
            {
                ResponsePayload = ByteString.CopyFrom(new byte[] { 0x10 })
            }
        };
        transport.Setup(t => t.SendMessageAsync(peerId, directSessionId, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse { OriginalResponse = response });

        var mismatched = new RelayOpaqueResponse { Version = 1, MessageAckId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()) };
        secureSvc.Setup(s => s.DecryptInboundAsync(new CryptoSelfId(selfId.Value), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sessionId, Plaintext.FromBytes(mismatched.ToByteArray())));

        var orchestratorWithQueries = new RelayOrchestrator(
            Mock.Of<ILogger<RelayOrchestrator>>(),
            queue.Object,
            directSessions.Object,
            secureSvc.Object,
            transport.Object,
            peerIdentityQueries.Object);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await orchestratorWithQueries.RelayNextAsync(selfId, peerId, CancellationToken.None));

        queue.Verify(q => q.DeleteByAckIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void RelayNextAsync_no_response_payload_throws()
    {
        var (orchestrator, queue, directSessions,  transport, secureSvc) = Create(out var active);
        var peerId = new Percolator.Identity.PeerId((uint)Random.Shared.Next(1, 1000000));
        var selfId = active.Identity!.SelfIdentityId;
        var ackId = Guid.NewGuid();
        var blob = new byte[] { 0x07 };
        var sessionGuid = Guid.NewGuid();
        var directSessionId = new DirectSessionId(sessionGuid);
        var networkPeerId = new NetworkPeerId(peerId.Value);
        var publicIdentityId = new Percolator.Identity.PublicIdentityId(Guid.NewGuid());

        var peerIdentityQueries = new Mock<IPeerIdentityQueries>(MockBehavior.Strict);
        peerIdentityQueries.Setup(q => q.GetPublicIdentityIdAsync(peerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(publicIdentityId);

        queue.Setup(q => q.FetchAsync(publicIdentityId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.List<(Guid, QueuedPayloadBytes)> { (ackId, QueuedPayloadBytes.FromBytesOwned(blob)) });
        directSessions.Setup(d => d.GetByRemotePeerIdAsync(networkPeerId, new NetworkSelfId(active.Identity!.SelfIdentityId.Value)))
            .ReturnsAsync(new DirectSession(networkPeerId, directSessionId));
        secureSvc.Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(new byte[] { 0xAA }));

        // No response payload
        transport.Setup(t => t.SendMessageAsync(peerId, directSessionId, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse { OriginalResponse = new DeliverOpaqueMessageResponse() });

        var orchestratorWithQueries = new RelayOrchestrator(
            Mock.Of<ILogger<RelayOrchestrator>>(),
            queue.Object,
            directSessions.Object,
            secureSvc.Object,
            transport.Object,
            peerIdentityQueries.Object);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await orchestratorWithQueries.RelayNextAsync(selfId, peerId, CancellationToken.None));
    }

    [Test]
    public async Task RelayNextAsync_empty_queue_returns_false()
    {
        var (orchestrator, queue, _, __, ___) = Create(out var active);
        var peerId = new Percolator.Identity.PeerId((uint)Random.Shared.Next(1, 1000000));
        var selfId = active.Identity!.SelfIdentityId;
        var publicIdentityId = new Percolator.Identity.PublicIdentityId(Guid.NewGuid());

        var peerIdentityQueries = new Mock<IPeerIdentityQueries>(MockBehavior.Strict);
        peerIdentityQueries.Setup(q => q.GetPublicIdentityIdAsync(peerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(publicIdentityId);

        queue.Setup(q => q.FetchAsync(publicIdentityId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.List<(Guid, QueuedPayloadBytes)>());

        var orchestratorWithQueries = new RelayOrchestrator(
            Mock.Of<ILogger<RelayOrchestrator>>(),
            queue.Object,
            Mock.Of<IDirectSessionRepository>(),
            Mock.Of<ISecureMessagingService>(),
            Mock.Of<IMessageTransportService>(),
            peerIdentityQueries.Object);

        var result = await orchestratorWithQueries.RelayNextAsync(selfId, peerId, CancellationToken.None);
        result.Should().BeFalse();
    }

    [Test]
    public void RelayNextAsync_no_session_throws()
    {
        var (orchestrator, queue, directSessions,  transport, secureSvc) = Create(out var active);
        var peerId = new Percolator.Identity.PeerId((uint)Random.Shared.Next(1, 1000000));
        var selfId = active.Identity!.SelfIdentityId;
        var ackId = Guid.NewGuid();
        var blob = new byte[] { 0x09 };
        var networkPeerId = new NetworkPeerId(peerId.Value);
        var publicIdentityId = new Percolator.Identity.PublicIdentityId(Guid.NewGuid());

        var peerIdentityQueries = new Mock<IPeerIdentityQueries>(MockBehavior.Strict);
        peerIdentityQueries.Setup(q => q.GetPublicIdentityIdAsync(peerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(publicIdentityId);

        queue.Setup(q => q.FetchAsync(publicIdentityId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.List<(Guid, QueuedPayloadBytes)> { (ackId, QueuedPayloadBytes.FromBytesOwned(blob)) });
        directSessions.Setup(d => d.GetByRemotePeerIdAsync(networkPeerId, new NetworkSelfId(active.Identity!.SelfIdentityId.Value)))
            .ReturnsAsync((DirectSession?)null);

        var orchestratorWithQueries = new RelayOrchestrator(
            Mock.Of<ILogger<RelayOrchestrator>>(),
            queue.Object,
            directSessions.Object,
            secureSvc.Object,
            transport.Object,
            peerIdentityQueries.Object);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await orchestratorWithQueries.RelayNextAsync(selfId, peerId, CancellationToken.None));
    }
}
