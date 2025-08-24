using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Cli;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Dht;
using Percolator.Identity;
using Percolator.Network;
using SessionId = Percolator.Cryptography.SessionId;
using NetworkPeerId = Percolator.Network.PeerId;

namespace Percolator.ApplicationIntegrationTests.Dht;

[TestFixture]
public class DhtProbeLoopbackTests : IntegrationTestBase
{
    private sealed class LoopbackTransport : IMessageTransportService
    {
        private readonly Func<DeliverOpaqueMessageRequest, Task<DeliverOpaqueMessageResponse>> _invoke;

        public LoopbackTransport(Func<DeliverOpaqueMessageRequest, Task<DeliverOpaqueMessageResponse>> invoke)
        { _invoke = invoke; }

        public async Task<DeliverOpaqueMessageResponse> SendMessageAsync(
            Percolator.Identity.PeerId recipientPeerId,
            Percolator.Chat.ValueObjects.ConversationId conversationId,
            Percolator.Cryptography.SessionRatchetMessage message,
            CancellationToken cancellationToken = default)
        {
            var request = new DeliverOpaqueMessageRequest
            {
                SessionId = conversationId.Value.ToString(),
                Payload = ByteString.CopyFrom(message.Value)
            };

            // Call directly into the remote service (no gRPC)
            return await _invoke(request);
        }
    }

    [Test]
    public async Task FindNode_EndToEnd_UsingLoopbackTransport_ShouldReturnResponse()
    {
        // SERVER HOST (remote process)
        var serverSessionManager = new Mock<IDirectSessionManager>();
        var serverDhtRepo = new Mock<IDhtNodeRepository>();
        var serverPeerConnRepo = new Mock<IPeerConnectionRepository>();

        // Shared identifiers between client and server for the same conversation
        var conversationId = Percolator.Chat.ValueObjects.ConversationId.NewId();
        var sessionId = new SessionId(conversationId.Value);

        // Server Receive: decrypt incoming request to Dht FindNode
        serverSessionManager
            .Setup(s => s.ReceiveMessageAsync(sessionId, It.IsAny<Percolator.Cryptography.SessionRatchetMessage>()))
            .ReturnsAsync(() =>
            {
                var req = new FindNodeRequest { TargetPeerId = ByteString.CopyFrom(SHA256.HashData(Guid.NewGuid().ToByteArray())) };
                var env = new InternalEnvelope { DhtEnvelope = new DhtEnvelope { FindNodeRequest = req } };
                return new Percolator.Cryptography.Plaintext(env.ToByteArray());
            });

        // Server DHT returns nodes
        var closer = new List<DhtNode>
        {
            new(new(SHA256.HashData(Guid.NewGuid().ToByteArray())), new DnsEndPoint("localhost", 59001), DateTimeOffset.UtcNow)
        };
        serverDhtRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(closer);

        // Server Encrypt response
        var responseCipher = new Percolator.Cryptography.SessionRatchetMessage(Guid.NewGuid().ToByteArray());
        var serverRemotePeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        serverSessionManager
            .Setup(s => s.EncryptMessageAsync(sessionId, It.IsAny<Percolator.Cryptography.Plaintext>()))
            .ReturnsAsync((serverRemotePeerId, responseCipher));

        // Server maps remote sender to a peer connection
        serverSessionManager
            .Setup(s => s.GetRemotePeerIdFromDirectMessage(sessionId))
            .ReturnsAsync(new Percolator.Identity.PeerId(Guid.NewGuid()));
        serverPeerConnRepo
            .Setup(r => r.GetByIdAsync(It.IsAny<NetworkPeerId>()))
            .ReturnsAsync(new PeerConnection(
                new NetworkPeerId(Guid.NewGuid()),
                new DirectMessagePublicKey(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                new[] { new GrpcEndPoint(new DnsEndPoint("localhost", 59001), DateTimeOffset.UtcNow) },
                Array.Empty<TlsCertificate>(),
                DateTimeOffset.UtcNow));

        using var serverHost = CreateHost(GetAvailablePort(), "LoopbackDht-Server", services =>
        {
            services.AddSingleton(serverSessionManager.Object);
            services.AddSingleton(serverDhtRepo.Object);
            services.AddSingleton(new Mock<IConversationRepository>().Object);
            services.AddSingleton(new Mock<IPeerRepository>().Object);
            services.AddSingleton<IPeerConnectionRepository>(serverPeerConnRepo.Object);
            services.AddSingleton<IDhtService, DhtService>();
            services.AddSingleton(new Mock<IX3DHOrchestrator>().Object);
            services.AddSingleton(new Mock<IX3DHManager>().Object);
            services.AddSingleton(new Mock<IPreKeyBundleRepository>().Object);
            services.AddSingleton(new Mock<Percolator.Cryptography.ISigningService>().Object);
            services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Percolator.Dht.Messages.FindNodeRequest).Assembly));
        });

        // CLIENT HOST (local process running orchestrator)
        var clientSessionManager = new Mock<IDirectSessionManager>();
        var clientConversationService = new Mock<IConversationService>();

        clientConversationService
            .Setup(s => s.CreateDirectConversationAsync(It.IsAny<DnsEndPoint>(), It.IsAny<string>()))
            .ReturnsAsync(conversationId);

        // Client encrypts request
        var clientRemotePeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var clientRequestCipher = new Percolator.Cryptography.SessionRatchetMessage(Guid.NewGuid().ToByteArray());
        clientSessionManager
            .Setup(s => s.EncryptMessageAsync(sessionId, It.IsAny<Percolator.Cryptography.Plaintext>()))
            .ReturnsAsync((clientRemotePeerId, clientRequestCipher));

        // Client decrypts response to internal FindNodeResponse envelope
        clientSessionManager
            .Setup(s => s.ReceiveMessageAsync(sessionId, It.IsAny<Percolator.Cryptography.SessionRatchetMessage>()))
            .ReturnsAsync(() =>
            {
                var resp = new FindNodeResponse();
                resp.CloserPeers.Add(new NodeInfo
                {
                    PeerId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                    Address = "localhost:59001"
                });
                var env = new InternalEnvelope { DhtEnvelope = new DhtEnvelope { FindNodeResponse = resp } };
                return new Percolator.Cryptography.Plaintext(env.ToByteArray());
            });

        // Build client host, wiring loopback transport to server's message service
        using var clientHost = await CreateAndInitializeHostAsync(GetAvailablePort(), "LoopbackDht-Client", identityName: "local", services =>
        {
            services.AddSingleton<IConversationService>(clientConversationService.Object);
            services.AddSingleton(clientSessionManager.Object);
            services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Percolator.Dht.Messages.FindNodeRequest).Assembly));
            services.AddSingleton<IMessageTransportService>(sp => new LoopbackTransport(async req =>
            {
                var svc = serverHost.Services.GetRequiredService<PercolatorMessageService>();
                return await svc.DeliverOpaqueMessage(req, new TestServerCallContext());
            }));
        });

        var mediator = clientHost.Services.GetRequiredService<IMediator>();

        // Act
        var endpoint = new DnsEndPoint("localhost", 5555);
        var result = await mediator.Send(new DhtProbeCommand(endpoint, "remote", SelfIdentityName: null), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.CloserPeers.Count.Should().BeGreaterThan(0);
    }
}
