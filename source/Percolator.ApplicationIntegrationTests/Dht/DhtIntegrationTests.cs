using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Dht;
using Percolator.Identity;
using Percolator.Network;
using PeerId = Percolator.Cryptography.Primitives.PeerId;
using SessionId = Percolator.Cryptography.SessionId;
using NetworkPeerId = Percolator.Network.PeerId;

namespace Percolator.ApplicationIntegrationTests.Dht;

[TestFixture]
public class DhtIntegrationTests : IntegrationTestBase
{
    [Test]
    public async Task DeliverOpaqueMessage_WhenReceivesPingRequest_ShouldUpdateDhtRepository()
    {
        // Arrange
        var dhtRepositoryMock = new Mock<IDhtNodeRepository>();
        var sessionManagerMock = new Mock<IDirectSessionManager>();
        var peerConnectionRepoMock = new Mock<IPeerConnectionRepository>();

        // Mocks for unused dependencies to allow the host to build
        var x3dhOrchestratorMock = new Mock<IX3DHOrchestrator>();
        var conversationRepoMock = new Mock<IConversationRepository>();
        var peerRepoMock = new Mock<IPeerRepository>();
        var x3dhManagerMock = new Mock<IX3DHManager>();
        var bundleRepoMock = new Mock<IPreKeyBundleRepository>();
        var signingServiceMock = new Mock<Percolator.Cryptography.ISigningService>();
        var peerTrustManagerMock = new Mock<IPeerTrustManager>();

        var port = GetAvailablePort();
        using var host = CreateHost(port, "DhtTest", services =>
        {
            services.AddSingleton(dhtRepositoryMock.Object);
            services.AddSingleton(sessionManagerMock.Object);
            services.AddSingleton(peerConnectionRepoMock.Object);
            services.AddSingleton(x3dhOrchestratorMock.Object);
            services.AddSingleton(conversationRepoMock.Object);
            services.AddSingleton(peerRepoMock.Object);
            services.AddSingleton(x3dhManagerMock.Object);
            services.AddSingleton(bundleRepoMock.Object);
            services.AddSingleton(signingServiceMock.Object);
            services.AddSingleton(peerTrustManagerMock.Object);
            services.AddMediatR(cfg => 
                cfg.RegisterServicesFromAssembly(typeof(Percolator.Dht.Messages.PingRequest).Assembly));
        });

        var messageService = host.Services.GetRequiredService<PercolatorMessageService>();

        var remotePeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var remoteSigningKey = new DirectMessagePublicKey(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("remote-peer")));
        var remoteEndpoint = new DnsEndPoint("localhost", 1234);
        var sessionId = new Percolator.Cryptography.SessionId(Guid.NewGuid());

        // 1. Mock the session manager to decrypt the message
        var dhtEnvelope = new DhtEnvelope { PingRequest = new Contracts.PingRequest() };
        var internalEnvelope = new InternalEnvelope { DhtEnvelope = dhtEnvelope };
        var ciphertext = new Ciphertext(new byte[1]); // Content doesn't matter
        sessionManagerMock.Setup(s => s.ReceiveMessageAsync(sessionId, It.IsAny<SessionRatchetMessage>()))
            .Returns(Task.FromResult<Plaintext?>(new Plaintext(internalEnvelope.ToByteArray())));

        // 2. Mock the session manager to return the peer ID
        sessionManagerMock.Setup(s => s.GetRemotePeerIdFromDirectMessage(sessionId))
            .ReturnsAsync(remotePeerId);

        // 3. Mock the peer connection repository to return connection info
        var networkPeerId = new NetworkPeerId(remotePeerId.Value);
        var connectionInfo = new PeerConnection(
            networkPeerId,
            remoteSigningKey,
            new[] { new GrpcEndPoint(remoteEndpoint, System.DateTimeOffset.UtcNow) },
            System.Array.Empty<TlsCertificate>(),
            System.DateTimeOffset.UtcNow);
        peerConnectionRepoMock.Setup(r => r.GetByIdAsync(networkPeerId))
            .Returns(Task.FromResult<PeerConnection?>(connectionInfo));

        var request = new DeliverOpaqueMessageRequest
        {
            SessionId = sessionId.Value.ToString(),
            Payload = ByteString.CopyFrom(ciphertext.Value) 
        };
        
        var nodeId = new NodeId(remoteSigningKey.Value);
        dhtRepositoryMock.Setup(r => r.GetAsync(nodeId)).ReturnsAsync(() => (DhtNode?)null);

        // Act
        await messageService.DeliverOpaqueMessage(request, Mock.Of<Grpc.Core.ServerCallContext>());

        // Assert: Verify the repository was called by the MediatR handler
        dhtRepositoryMock.Verify(r => r.GetAsync(It.Is<NodeId>(n => n.Value.SequenceEqual(remoteSigningKey.Value))), Times.Once);
        dhtRepositoryMock.Verify(r => r.AddAsync(It.Is<DhtNode>(n => 
            n.Id.Value.SequenceEqual(remoteSigningKey.Value) &&
            n.EndPoint.Equals(remoteEndpoint)
            )), Times.Once);
    }
}
