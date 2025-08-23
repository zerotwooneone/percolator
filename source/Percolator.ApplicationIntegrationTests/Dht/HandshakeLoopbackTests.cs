using System.Net;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NUnit.Framework;
using Percolator.Application.Dht;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Dht;
using Grpc.Net.Client;

namespace Percolator.ApplicationIntegrationTests.Dht;

[TestFixture]
public class HandshakeLoopbackTests : IntegrationTestBase
{
    private sealed class GrpcSessionLoopback : IGrpcSessionService
    {
        private readonly Func<EstablishDirectSessionRequest, Task<EstablishDirectSessionResponse>> _invoke;
        public GrpcSessionLoopback(Func<EstablishDirectSessionRequest, Task<EstablishDirectSessionResponse>> invoke)
        { _invoke = invoke; }

        public Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(DnsEndPoint endpoint, EstablishDirectSessionRequest request)
            => _invoke(request);
    }

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
            return await _invoke(request);
        }
    }

    [Test]
    public async Task FindNode_EndToEnd_WithHandshakeLoopback_ShouldReturnResponse()
    {
        // SERVER host with real session manager and a mocked DHT repo to ensure response content
        var serverPort = GetAvailablePort();
        using var serverHost = await CreateAndInitializeHostAsync(serverPort, "HandshakeLoopback-Server", identityName: "remote", additionalServiceRegistration: services =>
        {
            var serverDhtRepo = new Mock<IDhtNodeRepository>();
            serverDhtRepo
                .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<DhtNode>
                {
                    new(new(Guid.NewGuid().ToByteArray()), new DnsEndPoint("localhost", 59001), DateTimeOffset.UtcNow)
                });
            services.AddSingleton(serverDhtRepo.Object);
        });
        await serverHost.StartAsync();

        // CLIENT host uses real ConversationService but loops back both handshake and message delivery
        var clientPort = GetAvailablePort();
        using var clientHost = await CreateAndInitializeHostAsync(clientPort, "HandshakeLoopback-Client", identityName: "local", additionalServiceRegistration: services =>
        {
            var channel = GrpcChannel.ForAddress($"http://localhost:{serverPort}");
            var transportClient = new TransportService.TransportServiceClient(channel);

            services.AddSingleton<IGrpcSessionService>(sp => new GrpcSessionLoopback(async req =>
            {
                return await transportClient.EstablishDirectSessionAsync(req).ResponseAsync;
            }));

            services.AddSingleton<IMessageTransportService>(sp => new LoopbackTransport(async req =>
            {
                var svc = serverHost.Services.GetRequiredService<PercolatorMessageService>();
                // Provide a realistic peer string; HttpContext is not used in DeliverOpaqueMessage
                return await svc.DeliverOpaqueMessage(req, new TestServerCallContext($"ipv4:localhost:{clientPort}"));
            }));
        });

        var orchestrator = clientHost.Services.GetRequiredService<IDhtProbeOrchestrator>();

        // Act: null targetPeerId to force orchestrator to hash ActiveIdentityContext key
        var result = await orchestrator.FindNodeAsync(new DnsEndPoint("localhost", 5555), "remote", targetPeerId: null);

        // Assert
        result.Should().NotBeNull();
        result.CloserPeers.Count.Should().BeGreaterThan(0);
    }
}
