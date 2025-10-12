using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Application.Network;
using Percolator.Cryptography;
using Percolator.Dht;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
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
        var directSessionRepoMock = new Mock<IDirectSessionRepository>();

        // Mocks for unused dependencies to allow the host to build
        var x3dhOrchestratorMock = new Mock<IX3DHOrchestrator>();
        var conversationRepoMock = new Mock<IConversationRepository>();
        var peerRepoMock = new Mock<IPeerRepository>();
        var x3dhManagerMock = new Mock<IX3DHManager>();
        var bundleRepoMock = new Mock<IPreKeyBundleRepository>();
        var signingServiceMock = new Mock<Percolator.Cryptography.ISigningService>();
        var peerTrustManagerMock = new Mock<IPeerTrustManager>();

        // Build a valid ratchet payload header and register a lookup mock that resolves it
        var sessionId = new Percolator.Cryptography.SessionId(Guid.NewGuid());
        var headerKey = SHA256.HashData(Guid.NewGuid().ToByteArray());
        var ratchetPayload = new RatchetMessage
        {
            Header = new RatchetHeader
            {
                RatchetKey = ByteString.CopyFrom(headerKey),
                Counter = 0
            },
            Ciphertext = ByteString.CopyFrom(new byte[] { 1, 2, 3 })
        };
        var port = GetAvailablePort();
        using var host = CreateHost(port, "DhtTest",  services =>
        {
            services.AddSingleton<IDhtNodeRepository>(dhtRepositoryMock.Object);
            services.AddSingleton<IDirectSessionManager>(sessionManagerMock.Object);
            services.AddSingleton<IPeerConnectionRepository>(peerConnectionRepoMock.Object);
            services.AddSingleton<IDirectSessionRepository>(directSessionRepoMock.Object);
            // Fast-path lookup resolves our header key
            var ratchetLookup = new Moq.Mock<IRatchetKeySessionLookup>();
            var preKey = new PreKey(headerKey);
            ratchetLookup.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DirectSessionId(sessionId.Value));
            ratchetLookup.Setup(l => l.UpsertAsync(It.IsAny<DirectSessionId>(), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            services.AddSingleton<IRatchetKeySessionLookup>(ratchetLookup.Object);
            services.AddSingleton<IX3DHOrchestrator>(x3dhOrchestratorMock.Object);
            services.AddSingleton<IConversationRepository>(conversationRepoMock.Object);
            services.AddSingleton<IPeerRepository>(peerRepoMock.Object);
            services.AddSingleton<IX3DHManager>(x3dhManagerMock.Object);
            services.AddSingleton<IPreKeyBundleRepository>(bundleRepoMock.Object);
            services.AddSingleton<Percolator.Cryptography.ISigningService>(signingServiceMock.Object);
            services.AddSingleton<IPeerTrustManager>(peerTrustManagerMock.Object);
            // Ensure ActiveIdentityContext has an identity with SelfIdentityId set
            services.AddSingleton(new Percolator.Application.Identity.ActiveIdentityContext
            {
                Identity = new IdentityRecord(Guid.NewGuid(), "Test") { SelfIdentityId = 1 }
            });
            services.AddMediatR(cfg => 
                cfg.RegisterServicesFromAssembly(typeof(Percolator.Dht.Messages.PingRequest).Assembly));
        });

        var messageService = host.Services.GetRequiredService<PercolatorMessageService>();

        var remotePeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var remoteSigningKey = new DirectMessagePublicKey(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("remote-peer")));
        var remoteEndpoint = new DnsEndPoint("localhost", 1234);

        // 1. Mock the session manager to decrypt the message
        var dhtEnvelope = new DhtEnvelope { PingRequest = new Contracts.PingRequest() };
        var internalEnvelope = new InternalEnvelope { DhtEnvelope = dhtEnvelope };
        var ciphertext = new Ciphertext(new byte[1]); // Content doesn't matter
        sessionManagerMock.Setup(s => s.ReceiveMessageAsync(sessionId, It.IsAny<SessionRatchetMessage>()))
            .Returns(Task.FromResult<Plaintext?>(new Plaintext(internalEnvelope.ToByteArray())));

        // 2. Mock the direct session repository to map session to remote peer
        directSessionRepoMock.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId.Value), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new NetworkPeerId(remotePeerId.Value), new DirectSessionId(sessionId.Value)));

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
            Payload = ByteString.CopyFrom(ratchetPayload.ToByteArray())
        };
        
        // Production derives NodeId as SHA-256 of the signing key bytes (SPKI). Reflect that here.
        var nodeId = new NodeId(SHA256.HashData(remoteSigningKey.Value));
        dhtRepositoryMock.Setup(r => r.GetAsync(nodeId)).ReturnsAsync(() => (DhtNode?)null);

        // Act
        await messageService.DeliverOpaqueMessage(request, new TestServerCallContext());

        // Assert: Verify the repository was called by the MediatR handler
        dhtRepositoryMock.Verify(r => r.GetAsync(It.Is<NodeId>(n => n.Value.SequenceEqual(SHA256.HashData(remoteSigningKey.Value)))), Times.Once);
        dhtRepositoryMock.Verify(r => r.AddAsync(It.Is<DhtNode>(n => 
            n.Id.Value.SequenceEqual(SHA256.HashData(remoteSigningKey.Value)) &&
            n.EndPoint.Equals(remoteEndpoint)
            )), Times.Once);
    }

    [Test]
    public async Task DeliverOpaqueMessage_WhenReceivesFindNodeRequest_ShouldReturnCloserNodesInResponsePayload()
    {
        // Arrange
        var dhtNodeRepoMock = new Mock<IDhtNodeRepository>();
        var sessionManagerMock = new Mock<IDirectSessionManager>();
        var peerConnectionRepoMock = new Mock<IPeerConnectionRepository>();
        var directSessionRepoMock = new Mock<IDirectSessionRepository>();
        var sessionId = new Percolator.Cryptography.SessionId(Guid.NewGuid());

        // Build a valid ratchet payload header and register a lookup mock that resolves it
        var headerKey2 = SHA256.HashData(Guid.NewGuid().ToByteArray());
        var ratchetPayload2 = new RatchetMessage
        {
            Header = new RatchetHeader
            {
                RatchetKey = ByteString.CopyFrom(headerKey2),
                Counter = 0
            },
            Ciphertext = ByteString.CopyFrom(new byte[] { 7, 8, 9 })
        };
        var port = GetAvailablePort();
        using var host = CreateHost(port, "DhtTest", services =>
        {
            services.AddSingleton(dhtNodeRepoMock.Object);
            services.AddSingleton(sessionManagerMock.Object);
            services.AddSingleton(peerConnectionRepoMock.Object);
            services.AddSingleton(directSessionRepoMock.Object);
            services.AddSingleton<IDhtService, DhtService>();
            services.AddSingleton(new Mock<IConversationRepository>().Object);
            // Ensure ActiveIdentityContext has an identity with SelfIdentityId set
            services.AddSingleton(new Percolator.Application.Identity.ActiveIdentityContext
            {
                Identity = new IdentityRecord(Guid.NewGuid(), "Test") { SelfIdentityId = 1 }
            });
            // Fast-path lookup resolves our header key
            var ratchetLookup2 = new Moq.Mock<IRatchetKeySessionLookup>();
            var preKey2 = new PreKey(headerKey2);
            ratchetLookup2.Setup(l => l.TryResolveAsync(It.Is<PreKey>(p => p.Value.SequenceEqual(headerKey2)), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DirectSessionId(sessionId.Value));
            ratchetLookup2.Setup(l => l.UpsertAsync(It.IsAny<DirectSessionId>(), It.IsAny<int>(), It.IsAny<PreKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            services.AddSingleton<IRatchetKeySessionLookup>(ratchetLookup2.Object);
            services.AddMediatR(cfg =>
                cfg.RegisterServicesFromAssembly(typeof(Percolator.Dht.Messages.FindNodeRequest).Assembly));
        });

        var messageService = host.Services.GetRequiredService<PercolatorMessageService>();

        var remotePeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var targetId = new NodeId(SHA256.HashData(Guid.NewGuid().ToByteArray()));

        // 1. Mock the session manager to decrypt the message
        var findNodeRequestProto = new Contracts.FindNodeRequest { TargetPeerId = ByteString.CopyFrom(targetId.Value) };
        var dhtEnvelope = new DhtEnvelope { FindNodeRequest = findNodeRequestProto };
        var internalEnvelope = new InternalEnvelope { DhtEnvelope = dhtEnvelope };

        sessionManagerMock.Setup(s => s.ReceiveMessageAsync(It.Is<SessionId>(sid => sid == sessionId), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(new Plaintext(internalEnvelope.ToByteArray()));

        // 2. Mock the direct session repository to map session to remote peer
        directSessionRepoMock.Setup(r => r.GetBySessionIdAsync(new DirectSessionId(sessionId.Value), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new NetworkPeerId(remotePeerId.Value), new DirectSessionId(sessionId.Value)));

        // 3. Mock the peer connection repository to return connection info for the remote peer
        var networkPeerId = new NetworkPeerId(remotePeerId.Value);
        var connectionInfo = new PeerConnection(
            networkPeerId,
            new DirectMessagePublicKey(SHA256.HashData(Guid.NewGuid().ToByteArray())),
            new List<GrpcEndPoint> { new(new DnsEndPoint("localhost", 5000), System.DateTimeOffset.UtcNow) },
            System.Array.Empty<TlsCertificate>(),
            System.DateTimeOffset.UtcNow);
        peerConnectionRepoMock.Setup(r => r.GetByIdAsync(networkPeerId))
            .ReturnsAsync(connectionInfo);

        // 4. Mock the DHT repository to return a list of closer nodes
        var closerNodes = new List<DhtNode>
        {
            new(new(SHA256.HashData(Guid.NewGuid().ToByteArray())), new DnsEndPoint("localhost", 5001), System.DateTimeOffset.UtcNow),
            new(new(SHA256.HashData(Guid.NewGuid().ToByteArray())), new DnsEndPoint("localhost", 5002), System.DateTimeOffset.UtcNow)
        };
        dhtNodeRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(closerNodes);

        // 5. Mock the session manager's encryption call for the response
        var expectedResponsePayload = new SessionRatchetMessage(Guid.NewGuid().ToByteArray());
        sessionManagerMock.Setup(s => s.EncryptMessageAsync(sessionId, It.IsAny<Plaintext>()))
            .ReturnsAsync(expectedResponsePayload);

        var request = new DeliverOpaqueMessageRequest
        {
            Payload = ByteString.CopyFrom(ratchetPayload2.ToByteArray())
        };

        // Act
        var response = await messageService.DeliverOpaqueMessage(request, new TestServerCallContext());

        // Assert
        response.Should().NotBeNull();
        response.ResultCase.Should().Be(DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload);
        response.ResponsePayload.Should().NotBeNull();
        response.ResponsePayload.HasResponsePayload.Should().BeTrue();
        response.ResponsePayload.ResponsePayload.ToByteArray().Should().BeEquivalentTo(expectedResponsePayload.Value);
    }
}
