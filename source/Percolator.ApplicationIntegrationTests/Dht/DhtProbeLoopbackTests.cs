using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

// Public fake handler to bypass Double Ratchet in this end-to-end loopback test
public sealed class FakeDeliverOpaqueMessageHandler : IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>
{
    public Task<DeliverOpaqueMessageResult> Handle(DeliverOpaqueMessageCommand request, CancellationToken cancellationToken)
    {
        // Interpret payload bytes directly as InternalEnvelope
        var env = InternalEnvelope.Parser.ParseFrom(request.PayloadBytes);
        if (env.DhtEnvelope?.FindNodeRequest == null)
        {
            // No-op
            return Task.FromResult(new DeliverOpaqueMessageResult());
        }

        var resp = new InternalEnvelope
        {
            DhtEnvelope = new DhtEnvelope
            {
                FindNodeResponse = new FindNodeResponse()
            }
        };
        // Add a dummy node
        resp.DhtEnvelope.FindNodeResponse.CloserPeers.Add(new NodeInfo
        {
            PeerId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Address = "localhost:59001"
        });

        return Task.FromResult(new DeliverOpaqueMessageResult
        {
            ResponsePayloadBytes = resp.ToByteArray()
        });
    }
}

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
        var serverDirectSessionRepo = new Mock<IDirectSessionRepository>();

        // Shared identifiers between client and server for the same conversation
        var conversationId = Percolator.Chat.ValueObjects.ConversationId.NewId();
        var sessionId = new SessionId(conversationId.Value);

        // Server Receive: decrypt incoming request to Dht FindNode
        serverSessionManager
            .Setup(s => s.ReceiveMessageAsync(It.IsAny<SessionId>(), It.IsAny<Percolator.Cryptography.SessionRatchetMessage>()))
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

        // Server Encrypt response (bypassed by fake handler, but keep stub)
        var responseCipher = new Percolator.Cryptography.SessionRatchetMessage(Guid.NewGuid().ToByteArray());
        serverSessionManager
            .Setup(s => s.EncryptMessageAsync(It.IsAny<SessionId>(), It.IsAny<Percolator.Cryptography.Plaintext>()))
            .ReturnsAsync(responseCipher);

        // Server maps remote sender via direct session repository
        var serverNetworkPeerGuid = Guid.NewGuid();
        serverDirectSessionRepo
            .Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId.Value)))
            .ReturnsAsync(new DirectSession(new NetworkPeerId(serverNetworkPeerGuid), new DirectSessionId(sessionId.Value)));
        serverPeerConnRepo
            .Setup(r => r.GetByIdAsync(It.IsAny<NetworkPeerId>()))
            .ReturnsAsync((NetworkPeerId pid) =>
                pid.Value == serverNetworkPeerGuid
                    ? new PeerConnection(
                        new NetworkPeerId(serverNetworkPeerGuid),
                        new DirectMessagePublicKey(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                        new[] { new GrpcEndPoint(new DnsEndPoint("localhost", 59001), DateTimeOffset.UtcNow) },
                        Array.Empty<TlsCertificate>(),
                        DateTimeOffset.UtcNow)
                    : null);

        using var serverHost = CreateHost(GetAvailablePort(), "LoopbackDht-Server", services =>
        {
            services.RemoveAll<IDirectSessionManager>();
            services.AddSingleton<IDirectSessionManager>(serverSessionManager.Object);
            services.RemoveAll<DeliverOpaqueMessageHandler>();
            services.RemoveAll<IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>>();
            services.AddTransient<IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>, FakeDeliverOpaqueMessageHandler>();
            services.Replace(ServiceDescriptor.Singleton<IDhtNodeRepository>(sp => serverDhtRepo.Object));
            services.Replace(ServiceDescriptor.Singleton<IConversationRepository>(sp => new Mock<IConversationRepository>().Object));
            services.Replace(ServiceDescriptor.Singleton<IPeerRepository>(sp => new Mock<IPeerRepository>().Object));
            services.Replace(ServiceDescriptor.Singleton<IPeerConnectionRepository>(sp => serverPeerConnRepo.Object));
            services.Replace(ServiceDescriptor.Singleton<IDirectSessionRepository>(sp => serverDirectSessionRepo.Object));
            services.AddSingleton<IDhtService, DhtService>();
            services.Replace(ServiceDescriptor.Singleton<IX3DHOrchestrator>(sp => new Mock<IX3DHOrchestrator>().Object));
            services.Replace(ServiceDescriptor.Singleton<IX3DHManager>(sp => new Mock<IX3DHManager>().Object));
            services.Replace(ServiceDescriptor.Singleton<IPreKeyBundleRepository>(sp => new Mock<IPreKeyBundleRepository>().Object));
            services.Replace(ServiceDescriptor.Singleton<Percolator.Cryptography.ISigningService>(sp => new Mock<Percolator.Cryptography.ISigningService>().Object));
        });

        // Verify DI overrides on server
        var resolvedSm = serverHost.Services.GetRequiredService<IDirectSessionManager>();
        resolvedSm.Should().BeSameAs(serverSessionManager.Object);
        var resolvedHandler = serverHost.Services.GetRequiredService<IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>>();
        resolvedHandler.Should().BeOfType<FakeDeliverOpaqueMessageHandler>();

        // CLIENT HOST (local process running orchestrator)
        var clientSessionManager = new Mock<IDirectSessionManager>();
        var clientConversationService = new Mock<IConversationService>();

        clientConversationService
            .Setup(s => s.GetExistingDirectConversationAsync(It.IsAny<Peer>()))
            .ReturnsAsync(conversationId);

        // Client encrypts request: return an InternalEnvelope with Dht FindNodeRequest directly as bytes
        var findReq = new FindNodeRequest { TargetPeerId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()) };
        var clientReqEnvelope = new InternalEnvelope { DhtEnvelope = new DhtEnvelope { FindNodeRequest = findReq } };
        var clientRequestCipher = new Percolator.Cryptography.SessionRatchetMessage(clientReqEnvelope.ToByteArray());
        clientSessionManager
            .Setup(s => s.EncryptMessageAsync(It.IsAny<SessionId>(), It.IsAny<Percolator.Cryptography.Plaintext>()))
            .ReturnsAsync(clientRequestCipher);

        // Client decrypts response to internal FindNodeResponse envelope
        clientSessionManager
            .Setup(s => s.ReceiveMessageAsync(It.IsAny<SessionId>(), It.IsAny<Percolator.Cryptography.SessionRatchetMessage>()))
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
            services.Replace(ServiceDescriptor.Singleton<IConversationService>(sp => clientConversationService.Object));
            services.RemoveAll<IDirectSessionManager>();
            services.AddSingleton<IDirectSessionManager>(clientSessionManager.Object);
            // DhtProbeHandler now depends on IPeerRepository; provide a simple mock returning a Peer by name
            var clientPeerRepo = new Mock<IPeerRepository>();
            clientPeerRepo.Setup(r => r.GetByNameAsync(It.IsAny<string>()))
                .ReturnsAsync((string name) => new Peer(new Percolator.Identity.PeerId(Guid.NewGuid()), name));
            services.Replace(ServiceDescriptor.Singleton<IPeerRepository>(sp => clientPeerRepo.Object));
            services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Percolator.Dht.Messages.FindNodeRequest).Assembly));
            services.AddSingleton<IMessageTransportService>(sp => new LoopbackTransport(async req =>
            {
                var handler = serverHost.Services.GetRequiredService<IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>>();
                var cmd = new DeliverOpaqueMessageCommand
                {
                    SessionId = Guid.Parse(req.SessionId),
                    PayloadBytes = req.Payload.ToByteArray()
                };
                var result = await handler.Handle(cmd, CancellationToken.None);
                var response = new DeliverOpaqueMessageResponse();
                if (result.ResponsePayloadBytes is not null)
                {
                    response.ResponsePayload = ByteString.CopyFrom(result.ResponsePayloadBytes);
                }
                return response;
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
