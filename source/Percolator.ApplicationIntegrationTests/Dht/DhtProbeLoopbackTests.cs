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
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Dht;
using Percolator.Network;
using SessionId = Percolator.Cryptography.SessionId;
using NetworkPeerId = Percolator.Network.PeerId;

namespace Percolator.ApplicationIntegrationTests.Dht;

// Public fake handler to bypass Double Ratchet in this end-to-end loopback test
public sealed class FakeDeliverOpaqueMessageHandler : IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>
{
    public Task<DeliverOpaqueMessageResult> Handle(DeliverOpaqueMessageCommand request, CancellationToken cancellationToken)
    {
        // Always respond with a FindNodeResponse containing a dummy node
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

// Fake network sender that synchronously invokes the server's DeliverOpaqueMessage handler
public sealed class FakeNetworkSender : Percolator.Network.Messaging.INetworkSender
{
    private readonly IServiceProvider _serverProvider;
    public FakeNetworkSender(IServiceProvider serverProvider) => _serverProvider = serverProvider;

    public async Task<Percolator.Network.Messaging.SendOutcome> SendAsync(
        int selfIdentityId,
        Percolator.Network.PeerId target,
        Percolator.Network.Messaging.NetworkPayload payload,
        Percolator.Network.Messaging.SendStrategy strategy,
        CancellationToken ct = default)
    {
        var handler = _serverProvider.GetRequiredService<IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>>();
        var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = payload.Value.ToArray(), SelfIdentityId = new Percolator.Identity.SelfId(selfIdentityId) };
        var result = await handler.Handle(cmd, ct);
        return new Percolator.Network.Messaging.SendOutcome
        {
            Success = true,
            Path = "Direct",
            AttemptedPaths = new[] { "Direct" },
            Attempts = 1,
            ResponsePayload = result.ResponsePayloadBytes is null
                ? null
                : new Percolator.Network.Messaging.NetworkPayload(result.ResponsePayloadBytes)
        };
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

        public async Task<SendMessageResponse> SendMessageAsync(
            Percolator.Identity.PeerId recipientPeerId,
            Percolator.Network.DirectSessionId directSessionId,
            Percolator.Cryptography.SessionRatchetMessage message,
            CancellationToken cancellationToken = default)
        {
            var request = new DeliverOpaqueMessageRequest
            {
                Payload = ByteString.CopyFrom(message.Value)
            };

            // Call directly into the remote service (no gRPC)
            return new SendMessageResponse { OriginalResponse = await _invoke(request) };
        }
    }

    [Ignore("implementation tests not working yet")]
    [Test]
    public async Task FindNode_EndToEnd_UsingLoopbackTransport_ShouldReturnResponse()
    {
        // SERVER HOST (remote process)
        var serverDhtRepo = new Mock<IDhtNodeRepository>();
        var serverSecureSvc = new Mock<Percolator.Application.Services.ISecureMessagingService>();
        var serverDirectSessionRepo = new Mock<IDirectSessionRepository>();
        serverDirectSessionRepo
            .Setup(r => r.ListAsync(It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<DirectSession>());

        // Shared identifiers between client and server for the same direct session
        var directSessionId = new Percolator.Network.DirectSessionId(Guid.NewGuid());
        var sessionId = new SessionId(directSessionId.Value);

        // Server Receive: if used, decrypt inbound via SecureMessagingService
        serverSecureSvc
            .Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<Percolator.Cryptography.SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var req = new FindNodeRequest { TargetPeerId = ByteString.CopyFrom(SHA256.HashData(Guid.NewGuid().ToByteArray())) };
                var env = new InternalEnvelope { DhtEnvelope = new DhtEnvelope { FindNodeRequest = req } };
                return (new SessionId(Guid.NewGuid()), new Percolator.Cryptography.Plaintext(env.ToByteArray()));
            });

        // Server DHT returns nodes
        var closer = new List<DhtNode>
        {
            new(new(SHA256.HashData(Guid.NewGuid().ToByteArray())), new DnsEndPoint("localhost", 59001), DateTimeOffset.UtcNow)
        };
        serverDhtRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(closer);

        // Server Encrypt response (bypassed by fake handler, but keep stub)
        var responseCipher = new Percolator.Cryptography.SessionRatchetMessage(Guid.NewGuid().ToByteArray());
        serverSecureSvc
            .Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Percolator.Cryptography.Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(responseCipher);

        // Server maps remote sender via direct session repository
        var serverNetworkPeerGuid = Guid.NewGuid();
        serverDirectSessionRepo
            .Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId.Value), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new NetworkPeerId(serverNetworkPeerGuid), new DirectSessionId(sessionId.Value)));
        // Provide routing profile repo/planner for server
        var serverProfileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
        serverProfileRepo.Setup(r => r.GetByIdAsync(It.IsAny<NetworkPeerId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PeerRoutingProfile?)null);
        var serverPlanner = new Mock<IProfileRoutePlanner>(MockBehavior.Loose);

        using var serverHost = CreateHost(GetAvailablePort(), "LoopbackDht-Server", services =>
        {
            services.RemoveAll<DeliverOpaqueMessageHandler>();
            services.RemoveAll<IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>>();
            services.AddTransient<IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>, FakeDeliverOpaqueMessageHandler>();
            services.Replace(ServiceDescriptor.Singleton<Percolator.Application.Services.ISecureMessagingService>(sp => serverSecureSvc.Object));
            services.Replace(ServiceDescriptor.Singleton<IDhtNodeRepository>(sp => serverDhtRepo.Object));
            services.Replace(ServiceDescriptor.Singleton<IConversationRepository>(sp => new Mock<IConversationRepository>().Object));
            services.Replace(ServiceDescriptor.Singleton<IPeerRoutingProfileRepository>(sp => serverProfileRepo.Object));
            services.Replace(ServiceDescriptor.Singleton<IProfileRoutePlanner>(sp => serverPlanner.Object));
            services.Replace(ServiceDescriptor.Singleton<IDirectSessionRepository>(sp => serverDirectSessionRepo.Object));
            services.AddSingleton<IDhtService, DhtService>();
            services.Replace(ServiceDescriptor.Singleton<IPreKeyBundleRepository>(sp => new Mock<IPreKeyBundleRepository>().Object));
            services.Replace(ServiceDescriptor.Singleton<Percolator.Cryptography.ISigningService>(sp => new Mock<Percolator.Cryptography.ISigningService>().Object));
        });

        // Verify DI overrides on server
        var resolvedHandler = serverHost.Services.GetRequiredService<IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>>();
        resolvedHandler.Should().BeOfType<FakeDeliverOpaqueMessageHandler>();

        // CLIENT HOST (local process running orchestrator)
        var clientSecureSvc = new Mock<Percolator.Application.Services.ISecureMessagingService>();
        var clientSessionLocator = new Mock<IDirectSessionLocator>();

        clientSessionLocator
            .Setup(s => s.GetAsync(It.IsAny<Percolator.Identity.PeerId>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(directSessionId);

        // Client encrypts request: return an InternalEnvelope with Dht FindNodeRequest directly as bytes
        var findReq = new FindNodeRequest { TargetPeerId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()) };
        var clientReqEnvelope = new InternalEnvelope { DhtEnvelope = new DhtEnvelope { FindNodeRequest = findReq } };
        var clientRequestCipher = new Percolator.Cryptography.SessionRatchetMessage(clientReqEnvelope.ToByteArray());
        clientSecureSvc
            .Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Percolator.Cryptography.Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(clientRequestCipher);

        // Client decrypts response to internal FindNodeResponse envelope via SecureMessagingService
        clientSecureSvc
            .Setup(s => s.DecryptInboundAsync(It.IsAny<int>(), It.IsAny<Percolator.Cryptography.SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var resp = new FindNodeResponse();
                resp.CloserPeers.Add(new NodeInfo { PeerId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()), Address = "localhost:59001" });
                var env = new InternalEnvelope { DhtEnvelope = new DhtEnvelope { FindNodeResponse = resp } };
                return (new SessionId(Guid.NewGuid()), new Percolator.Cryptography.Plaintext(env.ToByteArray()));
            });

        // Build client host, wiring loopback transport to server's message service
        using var clientHost = await CreateAndInitializeHostAsync(GetAvailablePort(), "LoopbackDht-Client", identityName: "local", services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDirectSessionLocator>(sp => clientSessionLocator.Object));
            services.Replace(ServiceDescriptor.Singleton<Percolator.Application.Services.ISecureMessagingService>(sp => clientSecureSvc.Object));
            // Ensure MessageService can resolve an existing direct session without hitting a real store
            var clientDirectSessionRepo = new Mock<IDirectSessionRepository>();
            clientDirectSessionRepo
                .Setup(r => r.GetByRemotePeerIdAsync(It.IsAny<Network.PeerId>(), It.IsAny<int>()))
                .ReturnsAsync(new DirectSession(new NetworkPeerId(Guid.NewGuid()), directSessionId));
            clientDirectSessionRepo
                .Setup(r => r.ListAsync(It.IsAny<int>()))
                .ReturnsAsync(Array.Empty<DirectSession>());
            services.Replace(ServiceDescriptor.Singleton<IDirectSessionRepository>(sp => clientDirectSessionRepo.Object));
            // DhtProbeHandler depends on IPeerIdentityRepository; provide a mock that resolves any name
            var clientPeerIdentityRepo = new Mock<Percolator.Identity.IPeerIdentityRepository>();
            clientPeerIdentityRepo
                .Setup(r => r.GetByNameAsync(It.IsAny<Percolator.Identity.Model.DisplayName>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Percolator.Identity.Model.DisplayName dn, CancellationToken _) =>
                {
                    var pid = new Percolator.Identity.PeerId(Guid.NewGuid());
                    var id = new Percolator.Identity.Model.PeerIdentity(pid);
                    id.SetDisplayName(dn);
                    return id;
                });
            services.Replace(ServiceDescriptor.Singleton<Percolator.Identity.IPeerIdentityRepository>(sp => clientPeerIdentityRepo.Object));
            services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Percolator.Dht.Messages.PingRequest).Assembly));
            // Replace network sender to route directly to server fake handler and return payload
            services.Replace(ServiceDescriptor.Singleton<Percolator.Network.Messaging.INetworkSender>(sp => new FakeNetworkSender(serverHost.Services)));
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
