using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Percolator.Application.Dht;
using Moq;
using Percolator.Application.Sessions;
using Percolator.Application.Network;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Google.Protobuf;
using Percolator.Contracts;

namespace Percolator.ApplicationTests.Dht;

public class DhtProbeOrchestratorTests
{
    [Test]
    public void Can_Construct_DhtProbeOrchestrator()
    {
        var conversationService = new Mock<IConversationService>();
        var sessionManager = new Mock<IDirectSessionManager>();
        var transport = new Mock<IMessageTransportService>();
        var logger = new Mock<ILogger<DhtProbeOrchestrator>>();
        var activeIdentityContext = new ActiveIdentityContext();

        var orchestrator = new DhtProbeOrchestrator(
            conversationService.Object,
            sessionManager.Object,
            transport.Object,
            logger.Object,
            activeIdentityContext);

        orchestrator.Should().NotBeNull();
    }

    [Test]
    public async Task PingAsync_SendsOpaqueMessage()
    {
        var endpoint = new DnsEndPoint("localhost", 12345);
        var convId = ConversationId.NewId();

        var conversationService = new Mock<IConversationService>();
        conversationService
            .Setup(s => s.CreateDirectConversationAsync(endpoint, It.IsAny<string>()))
            .ReturnsAsync(convId);

        var sessionManager = new Mock<IDirectSessionManager>();
        var fakeRatchet = new SessionRatchetMessage(new byte[] { 1, 2, 3 });
        sessionManager
            .Setup(s => s.EncryptMessageAsync(It.IsAny<Percolator.Cryptography.SessionId>(), It.IsAny<Plaintext>()))
            .ReturnsAsync((new Percolator.Identity.PeerId(System.Guid.NewGuid()), fakeRatchet));

        var transport = new Mock<IMessageTransportService>();
        transport
            .Setup(t => t.SendMessageAsync(It.IsAny<Percolator.Identity.PeerId>(), convId, fakeRatchet, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new DeliverOpaqueMessageResponse { Version = 1 });

        var logger = new Mock<ILogger<DhtProbeOrchestrator>>();
        var activeIdentityContext = new ActiveIdentityContext();

        var orchestrator = new DhtProbeOrchestrator(
            conversationService.Object,
            sessionManager.Object,
            transport.Object,
            logger.Object,
            activeIdentityContext);

        await orchestrator.PingAsync(endpoint, "peer");

        transport.Verify(t => t.SendMessageAsync(It.IsAny<Percolator.Identity.PeerId>(), convId, fakeRatchet, It.IsAny<System.Threading.CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task FindNodeAsync_ParsesResponseAndReturnsPeers()
    {
        var endpoint = new DnsEndPoint("localhost", 12345);
        var convId = ConversationId.NewId();

        var conversationService = new Mock<IConversationService>();
        conversationService
            .Setup(s => s.CreateDirectConversationAsync(endpoint, It.IsAny<string>()))
            .ReturnsAsync(convId);

        var sessionManager = new Mock<IDirectSessionManager>();
        var fakeRatchet = new SessionRatchetMessage(new byte[] { 9, 9, 9 });
        sessionManager
            .Setup(s => s.EncryptMessageAsync(It.IsAny<Percolator.Cryptography.SessionId>(), It.IsAny<Plaintext>()))
            .ReturnsAsync((new Percolator.Identity.PeerId(System.Guid.NewGuid()), fakeRatchet));

        // Build a response InternalEnvelope with a DHT FindNodeResponse
        var respEnvelope = new InternalEnvelope
        {
            DhtEnvelope = new DhtEnvelope
            {
                FindNodeResponse = new FindNodeResponse()
            }
        };
        respEnvelope.DhtEnvelope.FindNodeResponse.CloserPeers.Add(new NodeInfo
        {
            PeerId = ByteString.CopyFrom(new byte[] { 0xAA, 0xBB }),
            Address = "127.0.0.1:5555"
        });

        var respPlain = new Plaintext(respEnvelope.ToByteArray());
        sessionManager
            .Setup(s => s.ReceiveMessageAsync(It.IsAny<Percolator.Cryptography.SessionId>(), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(respPlain);

        // Transport returns a response payload that will be decrypted by sessionManager
        var transport = new Mock<IMessageTransportService>();
        transport
            .Setup(t => t.SendMessageAsync(It.IsAny<Percolator.Identity.PeerId>(), convId, fakeRatchet, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new DeliverOpaqueMessageResponse
            {
                Version = 1,
                ResponsePayload = ByteString.CopyFrom(new byte[] { 0xDE, 0xAD }) // dummy; decrypted path uses sessionManager mock
            });

        var logger = new Mock<ILogger<DhtProbeOrchestrator>>();
        var activeIdentityContext = new ActiveIdentityContext();

        var orchestrator = new DhtProbeOrchestrator(
            conversationService.Object,
            sessionManager.Object,
            transport.Object,
            logger.Object,
            activeIdentityContext);

        var targetId = new byte[] { 0x01, 0x02, 0x03 };
        var result = await orchestrator.FindNodeAsync(endpoint, "peer", targetId);

        result.Should().NotBeNull();
        result.CloserPeers.Should().HaveCount(1);
        result.CloserPeers[0].Address.Should().Be("127.0.0.1:5555");
    }
}

