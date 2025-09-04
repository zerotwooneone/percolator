using System.Net;
using FluentAssertions;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Percolator.Application.Cli;
using Percolator.Application.Network;
using Percolator.Contracts;

namespace Percolator.ApplicationIntegrationTests.Dht;

[TestFixture]
public class DhtEndToEndTests : IntegrationTestBase
{
    private sealed class GrpcSessionLoopback : IGrpcSessionService
    {
        private readonly IServiceProvider _hostProvider;
        public GrpcSessionLoopback(IServiceProvider hostProvider) => _hostProvider = hostProvider;

        public async Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(DnsEndPoint endpoint, EstablishDirectSessionRequest request)
        {
            var mediator = _hostProvider.GetRequiredService<IMediator>();
            var payload = EstablishDirectSessionRequest.Types.DirectInitiatorPayload.Parser.ParseFrom(request.InitiatorBundle.SignedPayload);
            var command = new EstablishDirectSessionCommand
            {
                IdentitySigningKeyBytes = request.InitiatorBundle.IdentitySigningKey.ToByteArray(),
                SignedPayloadBytes = request.InitiatorBundle.SignedPayload.ToByteArray(),
                PayloadSignatureBytes = request.InitiatorBundle.PayloadSignature.ToByteArray(),
                OneTimePreKeyBytes = request.InitiatorBundle.HasOneTimePreKey ? request.InitiatorBundle.OneTimePreKey.ToByteArray() : null,
                PreKeyBytes = payload.SignedPreKey.ToByteArray(),
                PeerEndPoint = endpoint,
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
        }
    }

    private sealed class LoopbackTransport : IMessageTransportService
    {
        private readonly IServiceProvider _hostProvider;
        public LoopbackTransport(IServiceProvider hostProvider) => _hostProvider = hostProvider;

        public async Task<DeliverOpaqueMessageResponse> SendMessageAsync(
            Percolator.Identity.PeerId recipientPeerId,
            Percolator.Network.DirectSessionId directSessionId,
            Percolator.Cryptography.SessionRatchetMessage message,
            CancellationToken cancellationToken = default)
        {
            var mediator = _hostProvider.GetRequiredService<IMediator>();
            var cmd = new DeliverOpaqueMessageCommand
            {
                SessionId = directSessionId.Value,
                PayloadBytes = message.Value
            };
            var result = await mediator.Send(cmd, cancellationToken);
            var response = new DeliverOpaqueMessageResponse();
            if (result.ResponsePayloadBytes is not null)
            {
                response.ResponsePayload = ByteString.CopyFrom(result.ResponsePayloadBytes);
            }
            return response;
        }
    }

    [Test]
    public async Task PingAndFindNode_WithThreeNodes_ShouldDiscoverPeersViaHost()
    {
        // Arrange: Start Host
        var hostPort = GetAvailablePort();
        using var host = await CreateAndInitializeHostAsync(hostPort, "DhtE2E-Host", identityName: "host");

        // Build Alice with loopback transport to Host
        var alicePort = GetAvailablePort();
        using var alice = await CreateAndInitializeHostAsync(alicePort, "DhtE2E-Alice", identityName: "alice", additionalServiceRegistration: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGrpcSessionService>(sp => new GrpcSessionLoopback(host.Services)));
            services.Replace(ServiceDescriptor.Singleton<IMessageTransportService>(sp => new LoopbackTransport(host.Services)));
        });

        // Build Bob with loopback transport to Host
        var bobPort = GetAvailablePort();
        using var bob = await CreateAndInitializeHostAsync(bobPort, "DhtE2E-Bob", identityName: "bob", additionalServiceRegistration: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGrpcSessionService>(sp => new GrpcSessionLoopback(host.Services)));
            services.Replace(ServiceDescriptor.Singleton<IMessageTransportService>(sp => new LoopbackTransport(host.Services)));
        });

        var aliceMediator = alice.Services.GetRequiredService<IMediator>();
        var bobMediator = bob.Services.GetRequiredService<IMediator>();

        var hostEndpoint = new DnsEndPoint("localhost", hostPort);

        // Act 1: Alice probes Host (Ping + FindNode) -> expect initially no peers
        var aliceProbe1 = await aliceMediator.Send(new DhtProbeCommand(hostEndpoint, "host", SelfIdentityName: null));

        // Act 2: Bob probes Host (this will at least Ping; Host should record Bob)
        var bobProbe = await bobMediator.Send(new DhtProbeCommand(hostEndpoint, "host", SelfIdentityName: null));

        // Act 3: Alice probes Host again; Host should now return peers (including Bob)
        var aliceProbe2 = await aliceMediator.Send(new DhtProbeCommand(hostEndpoint, "host", SelfIdentityName: null));

        // Assert
        aliceProbe1.Should().NotBeNull();
        aliceProbe1.CloserPeers.Count.Should().BeGreaterThanOrEqualTo(0); // allow 0 at first

        bobProbe.Should().NotBeNull();
        // no strict assertion on bobProbe content; it may or may not include entries depending on Host state

        aliceProbe2.Should().NotBeNull();
        aliceProbe2.CloserPeers.Count.Should().BeGreaterThan(0);
    }
}
