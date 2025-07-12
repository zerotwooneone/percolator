using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Sessions;
using Percolator.Network;
using Percolator.Sessions;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using IdentityPeerId = Percolator.Identity.PeerId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using SessionPeerId = Percolator.Sessions.PeerId;
using TransportService = Percolator.Contracts.TransportService;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class ConversationServiceTests
{
    private Mock<IPeerRepository> _mockPeerRepository = null!;
    private Mock<ITlsCertificateService> _mockTlsCertificateService = null!;
    private Mock<IX3DHOrchestrator> _mockX3dhOrchestrator = null!;
    private Mock<IDirectSessionManager> _mockSessionManager = null!;
    private Mock<IConversationRepository> _mockConversationRepository = null!;
    private Mock<IPeerConnectionRepository> _mockPeerConnectionRepository = null!;
    private Mock<IOneTimeKeyProvider> _mockOneTimeKeyProvider = null!;
    private Mock<IHttpClientFactory> _mockHttpClientFactory = null!;
    private Mock<IPeerTrustManager> _mockPeerTrustManager = null!;
    private Mock<ILogger<ConversationService>> _mockLogger = null!;
    private ConversationService _service = null!;
    private ActiveIdentityContext _activeIdentityContext = null!;

    [SetUp]
    public void Setup()
    {
        _mockPeerRepository = new Mock<IPeerRepository>();
        _mockTlsCertificateService = new Mock<ITlsCertificateService>();
        _mockX3dhOrchestrator = new Mock<IX3DHOrchestrator>();
        _mockSessionManager = new Mock<IDirectSessionManager>();
        _mockOneTimeKeyProvider = new Mock<IOneTimeKeyProvider>();
        _mockConversationRepository = new Mock<IConversationRepository>();
        _mockPeerConnectionRepository = new Mock<IPeerConnectionRepository>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockPeerTrustManager = new Mock<IPeerTrustManager>();
        _mockLogger = new Mock<ILogger<ConversationService>>();
        _activeIdentityContext = new ActiveIdentityContext
        {
            Identity = new IdentityRecord(Guid.NewGuid(), "Test Identity"),
            Keys = new X3dhKeys(
                ECDsa.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
            )
        };

        _service = new ConversationService(
            _mockLogger.Object,
            _mockX3dhOrchestrator.Object,
            _mockSessionManager.Object,
            _mockConversationRepository.Object,
            _mockPeerRepository.Object,
            _mockPeerConnectionRepository.Object,
            _mockTlsCertificateService.Object,
            _mockOneTimeKeyProvider.Object,
            _activeIdentityContext,
            _mockHttpClientFactory.Object,
            _mockPeerTrustManager.Object
        );
    }

    // TODO: This test is disabled because it relies on the obsolete IGrpcClientFactory.
    // It needs to be refactored to mock HttpClient and its underlying message handlers to test the new implementation.
    /*
    [Test]
    public async Task CreateDirectConversationAsync_WhenPeerExists_EstablishesSessionAndCreatesConversation()
    {
        // Arrange
        var peerName = "existing-peer";
        var endpoint = new DnsEndPoint("localhost", 5000);
        var remoteIdentityKey = new byte[33];
        var localIdentity = new IdentityRecord(Guid.NewGuid(), "local-user");
        var localKeys = new X3dhKeys(ECDsa.Create(), ECDiffieHellman.Create(), ECDiffieHellman.Create());
        var peerId = new IdentityPeerId(Guid.NewGuid());
        var peer = new Peer(peerId, peerName);

        _activeIdentityContext.Identity = localIdentity;
        _activeIdentityContext.Keys = localKeys;

        _mockOneTimeKeyProvider.Setup(p => p.PopOneTimeKey()).Returns(ECDiffieHellman.Create());

        var responderBundle = new ContractsPreKeyBundle
        {
            IdentitySigningKey = ByteString.CopyFrom(remoteIdentityKey),
            IdentityAgreementKey = ByteString.CopyFrom(new byte[33]),
            SignedPreKey = ByteString.CopyFrom(new byte[33]),
            PreKeySignature = ByteString.CopyFrom(new byte[64])
        };

        var response = new EstablishSessionResponse { ResponderBundle = responderBundle };
        var fakeCall = TestCalls.AsyncUnaryCall(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        var mockTransportClient = new Mock<TransportService.TransportServiceClient>();
        mockTransportClient.Setup(c => c.EstablishSessionAsync(It.IsAny<EstablishSessionRequest>(), null, null, CancellationToken.None))
            .Returns(fakeCall);

        _mockTlsCertificateService.Setup(s => s.GetOrCreateTlsCertificateAsync(It.IsAny<string>(), It.IsAny<byte[]>()))
            .ReturnsAsync(new X509Certificate2());

        _mockPeerRepository.Setup(r => r.GetByNameAsync(peerName)).ReturnsAsync(peer);
        var sharedSecret = new SharedSecret(new byte[32]);
        _mockX3dhOrchestrator.Setup(x => x.CompleteHandshake(It.IsAny<ContractsPreKeyBundle>(), It.IsAny<ECDiffieHellman>()))
            .Returns(sharedSecret);

        // Act
        var conversationId = await _service.CreateDirectConversationAsync(endpoint, peerName);

        // Assert
        _mockSessionManager.Verify(s => s.EstablishSessionAsInitiatorAsync(
            It.IsAny<SessionConversationId>(),
            It.IsAny<SessionPeerId>(),
            It.IsAny<SessionIdentityKey>(),
            It.IsAny<SessionRatchetKey>(),
            sharedSecret), Times.Once);

        _mockConversationRepository.Verify(r => r.AddAsync(It.Is<ChatConversation>(c => c.Participants.Count == 2)), Times.Once);

        Assert.That(conversationId, Is.Not.EqualTo(default(ChatConversationId)));
    }
    */
}