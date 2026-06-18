using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Chat.MessageQueue;
using Percolator.Chat.ValueObjects;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using PeerId = Percolator.Network.PeerId;

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
            Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "Test", null) { SelfIdentityId = new SelfId(1) }
        };
        var orchestrator = new RelayOrchestrator(logger, queue.Object, directSessions.Object, secureSvc.Object, transport.Object);
        return (orchestrator, queue, directSessions, transport, secureSvc);
    }

    [Test]
    public async Task RelayNextAsync_happy_ack_deletes_and_returns_true()
    {
        var (orchestrator, queue, directSessions,  transport, secureSvc) = Create(out var active);
        var peerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var selfId = active.Identity!.SelfIdentityId;
        var ackId = Guid.NewGuid();
        var blob = new byte[] { 0x01, 0x02 };
        var sessionGuid = Guid.NewGuid();
        var sessionId = new SessionId(sessionGuid);
        var directSessionId = new DirectSessionId(sessionGuid);
        var networkPeerId = new PeerId(peerId.Value);

        queue.Setup(q => q.FetchAsync(peerId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.List<(Guid, QueuedPayloadBytes)> { (ackId, QueuedPayloadBytes.FromBytesOwned(blob)) });
        directSessions.Setup(d => d.GetByRemotePeerIdAsync(networkPeerId, active.Identity!.SelfIdentityId.Value))
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
        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sessionId, Plaintext.FromBytes(ack.ToByteArray())));

        queue.Setup(q => q.DeleteByAckIdAsync(ackId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await orchestrator.RelayNextAsync(selfId, peerId, CancellationToken.None);
        result.Should().BeTrue();
    }

    [Test]
    public void RelayNextAsync_ack_mismatch_throws()
    {
        var (orchestrator, queue, directSessions,  transport, secureSvc) = Create(out var active);
        var peerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var selfId = active.Identity!.SelfIdentityId;
        var ackId = Guid.NewGuid();
        var blob = new byte[] { 0x05 };
        var sessionGuid = Guid.NewGuid();
        var sessionId = new SessionId(sessionGuid);
        var directSessionId = new DirectSessionId(sessionGuid);
        var networkPeerId = new PeerId(peerId.Value);

        queue.Setup(q => q.FetchAsync(peerId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.List<(Guid, QueuedPayloadBytes)> { (ackId, QueuedPayloadBytes.FromBytesOwned(blob)) });
        directSessions.Setup(d => d.GetByRemotePeerIdAsync(networkPeerId, active.Identity!.SelfIdentityId.Value))
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
        secureSvc.Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sessionId, Plaintext.FromBytes(mismatched.ToByteArray())));

        Assert.ThrowsAsync<InvalidOperationException>(async () => await orchestrator.RelayNextAsync(selfId, peerId, CancellationToken.None));

        queue.Verify(q => q.DeleteByAckIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void RelayNextAsync_no_response_payload_throws()
    {
        var (orchestrator, queue, directSessions,  transport, secureSvc) = Create(out var active);
        var peerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var selfId = active.Identity!.SelfIdentityId;
        var ackId = Guid.NewGuid();
        var blob = new byte[] { 0x07 };
        var sessionGuid = Guid.NewGuid();
        var directSessionId = new DirectSessionId(sessionGuid);
        var networkPeerId = new PeerId(peerId.Value);

        queue.Setup(q => q.FetchAsync(peerId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.List<(Guid, QueuedPayloadBytes)> { (ackId, QueuedPayloadBytes.FromBytesOwned(blob)) });
        directSessions.Setup(d => d.GetByRemotePeerIdAsync(networkPeerId, active.Identity!.SelfIdentityId.Value))
            .ReturnsAsync(new DirectSession(networkPeerId, directSessionId));
        secureSvc.Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(new byte[] { 0xAA }));

        // No response payload
        transport.Setup(t => t.SendMessageAsync(peerId, directSessionId, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse { OriginalResponse = new DeliverOpaqueMessageResponse() });

        Assert.ThrowsAsync<InvalidOperationException>(async () => await orchestrator.RelayNextAsync(selfId, peerId, CancellationToken.None));
    }

    [Test]
    public async Task RelayNextAsync_empty_queue_returns_false()
    {
        var (orchestrator, queue, _, __, ___) = Create(out var active);
        var peerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var selfId = active.Identity!.SelfIdentityId;
        queue.Setup(q => q.FetchAsync(peerId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.List<(Guid, QueuedPayloadBytes)>());

        var result = await orchestrator.RelayNextAsync(selfId, peerId, CancellationToken.None);
        result.Should().BeFalse();
    }

    [Test]
    public void RelayNextAsync_no_session_throws()
    {
        var (orchestrator, queue, directSessions,  transport, secureSvc) = Create(out var active);
        var peerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var selfId = active.Identity!.SelfIdentityId;
        var ackId = Guid.NewGuid();
        var blob = new byte[] { 0x09 };
        var networkPeerId = new PeerId(peerId.Value);

        queue.Setup(q => q.FetchAsync(peerId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.List<(Guid, QueuedPayloadBytes)> { (ackId, QueuedPayloadBytes.FromBytesOwned(blob)) });
        directSessions.Setup(d => d.GetByRemotePeerIdAsync(networkPeerId, active.Identity!.SelfIdentityId.Value))
            .ReturnsAsync((DirectSession?)null);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await orchestrator.RelayNextAsync(selfId, peerId, CancellationToken.None));
    }
}
