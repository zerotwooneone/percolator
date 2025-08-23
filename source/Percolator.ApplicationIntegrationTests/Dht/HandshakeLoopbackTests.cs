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
using MediatR;

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
        Mock<IDhtNodeRepository>? serverDhtRepoRef = null;
        using var serverHost = await CreateAndInitializeHostAsync(serverPort, "HandshakeLoopback-Server", identityName: "remote", additionalServiceRegistration: services =>
        {
            // Register mock now; configure it after host starts when identity keys are available
            serverDhtRepoRef = new Mock<IDhtNodeRepository>();
            services.AddSingleton(serverDhtRepoRef.Object);
        });
        await serverHost.StartAsync();

        // After host start, derive NodeId from server's identity public signing key
        {
            var activeIdentity = serverHost.Services.GetRequiredService<Percolator.Application.Identity.ActiveIdentityContext>();
            var pubKey = activeIdentity.Keys!.IdentitySigningKey.ExportSubjectPublicKeyInfo();
            var nodeIdBytes = System.Security.Cryptography.SHA256.HashData(pubKey);
            var nodeId = new NodeId(nodeIdBytes);
            serverDhtRepoRef!.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<DhtNode>
                {
                    new(nodeId, new DnsEndPoint("localhost", 59001), DateTimeOffset.UtcNow)
                });
        }

        // CLIENT host uses real ConversationService but loops back both handshake and message delivery
        var clientPort = GetAvailablePort();
        using var clientHost = await CreateAndInitializeHostAsync(clientPort, "HandshakeLoopback-Client", identityName: "local", additionalServiceRegistration: services =>
        {
            var channel = GrpcChannel.ForAddress($"http://localhost:{serverPort}");
            var transportClient = new TransportService.TransportServiceClient(channel);

            services.AddSingleton<IGrpcSessionService>(sp => new GrpcSessionLoopback(async req =>
            {
                // Use serverHost mediator to handle handshake via EstablishDirectSessionHandler
                var mediator = serverHost.Services.GetRequiredService<IMediator>();
                var payload = EstablishDirectSessionRequest.Types.DirectInitiatorPayload.Parser.ParseFrom(req.InitiatorBundle.SignedPayload);
                var command = new EstablishDirectSessionCommand
                {
                    IdentitySigningKeyBytes = req.InitiatorBundle.IdentitySigningKey.ToByteArray(),
                    IdentityAgreementKeyBytes = req.InitiatorBundle.IdentityAgreementKey.ToByteArray(),
                    SignedPayloadBytes = req.InitiatorBundle.SignedPayload.ToByteArray(),
                    PayloadSignatureBytes = req.InitiatorBundle.PayloadSignature.ToByteArray(),
                    OneTimePreKeyBytes = req.InitiatorBundle.HasOneTimePreKey ? req.InitiatorBundle.OneTimePreKey.ToByteArray() : null,
                    PreKeyBytes = payload.SignedPreKey.ToByteArray(),
                    PeerEndPoint = new DnsEndPoint("localhost", clientPort),
                    ClientCertificate = null
                };
                var result = await mediator.Send(command);
                return new EstablishDirectSessionResponse
                {
                    Response = new EstablishDirectSessionResponse.Types.Response
                    {
                        IdentitySigningKey = ByteString.CopyFrom(result.IdentitySigningKeyBytes),
                        ResponsePayload = ByteString.CopyFrom(result.ResponsePayloadBytes),
                        PayloadSignature = ByteString.CopyFrom(result.PayloadSignatureBytes)
                    }
                };
            }));

            services.AddSingleton<IMessageTransportService>(sp => new LoopbackTransport(async req =>
            {
                // Use mediator to handle opaque message delivery
                var mediator = serverHost.Services.GetRequiredService<IMediator>();
                var result = await mediator.Send(new DeliverOpaqueMessageCommand
                {
                    SessionId = Guid.Parse(req.SessionId),
                    PayloadBytes = req.Payload.ToByteArray()
                });
                var response = new DeliverOpaqueMessageResponse();
                if (result.ResponsePayloadBytes is not null)
                {
                    response.ResponsePayload = ByteString.CopyFrom(result.ResponsePayloadBytes);
                }
                return response;
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
