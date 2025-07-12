using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
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
using Percolator.Network;
using Percolator.Sessions;
using ECDiffieHellman = System.Security.Cryptography.ECDiffieHellman;
using IdentityPeerId = Percolator.Identity.PeerId;
using NetworkPeerId = Percolator.Network.PeerId;
using SessionPeerId = Percolator.Sessions.PeerId;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using Signature = System.Security.Cryptography.Xml.Signature;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class ConversationServiceTests
{
    private Mock<IConversationRepository> _mockConversationRepo = null!;
    private Mock<IPeerRepository> _mockPeerRepo = null!;
    private Mock<IPeerConnectionRepository> _mockPeerConnectionRepo = null!;
    private Mock<ILocalPeerProvider> _mockLocalPeerProvider = null!;
    private X3DHOrchestrator _orchestrator = null!;
    private Mock<DirectSessionManager> _mockSessionManager = null!;
    private Mock<IGrpcClientFactory> _mockGrpcFactory = null!;
    private Mock<IX3DHManager> _mockX3dhManager = null!;
    private Mock<ITlsCertificateService> _mockTlsCertService = null!;
    private ActiveIdentityContext _activeIdentityContext = null!;
    private ConversationService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _mockConversationRepo = new Mock<IConversationRepository>();
        _mockPeerRepo = new Mock<IPeerRepository>();
        _mockPeerConnectionRepo = new Mock<IPeerConnectionRepository>();
        _mockLocalPeerProvider = new Mock<ILocalPeerProvider>();
        _mockGrpcFactory = new Mock<IGrpcClientFactory>();
        _mockX3dhManager = new Mock<IX3DHManager>();
        _mockTlsCertService = new Mock<ITlsCertificateService>();

        // Setup Active Identity
        var identitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var oneTimeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _activeIdentityContext = new ActiveIdentityContext
        {
            Identity = new IdentityRecord(Guid.Parse("c369a55d-fa2c-43d5-939a-712661a51c07"), "test-identity"),
            Keys = new X3dhKeys(identitySigningKey, identityAgreementKey, signedPreKey, new[] { oneTimeKey })
        };

        // Correctly setup concrete class dependencies
        _orchestrator = new X3DHOrchestrator(
            _activeIdentityContext,
            _mockX3dhManager.Object
        );

        _mockSessionManager = new Mock<DirectSessionManager>(
            Mock.Of<IDoubleRatchetSessionStore>(),
            _mockConversationRepo.Object,
            _mockLocalPeerProvider.Object,
            Mock.Of<IMessageStore>(),
            _activeIdentityContext
        );

        // Setup SUT
        _sut = new ConversationService(
            _activeIdentityContext,
            _orchestrator,
            _mockSessionManager.Object,
            _mockConversationRepo.Object,
            _mockPeerRepo.Object,
            _mockPeerConnectionRepo.Object,
            _mockLocalPeerProvider.Object,
            _mockGrpcFactory.Object,
            _mockTlsCertService.Object,
            new Mock<ILogger<ConversationService>>().Object
        );
    }

    [Test]
    public async Task CreateDirectConversationAsync_WhenPeerIsUnknownAndNoCertProvided_ShouldCreateAndSaveNewPeer()
    {
        // Arrange
        var peerName = "new-peer";
        var endpoint = new DnsEndPoint("localhost", 5001);
        var remoteIdentityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var remoteIdentityKeyBytes = remoteIdentityKey.ExportSubjectPublicKeyInfo();

        // Mock Peer Repository to show peer is unknown
        _mockPeerRepo.Setup(r => r.GetByNameAsync(peerName)).ReturnsAsync((Peer?)null);
        _mockPeerRepo.Setup(r => r.AddAsync(It.IsAny<Peer>()))
            .Returns(Task.CompletedTask);

        // Mock the IX3DHManager to simulate a successful handshake
        _mockX3dhManager.Setup(m => m.VerifySignature(It.IsAny<RatchetIdentityKey>(), It.IsAny<PreKey>(), It.IsAny<Percolator.Cryptography.Signature>()))
            .Returns(true);
        _mockX3dhManager.Setup(m => m.InitiateHandshake(It.IsAny<Cryptography.PreKeyBundle>(),
                It.IsAny<ECDiffieHellman>(), It.IsAny<ECDiffieHellman>()))
            .Returns(new SharedSecret(new byte[32]));

        // Mock gRPC client response
        var mockTransportClient = new Mock<TransportService.TransportServiceClient>();
        var responderBundle = new ContractsPreKeyBundle
        {
            IdentitySigningKey = ByteString.CopyFrom(remoteIdentityKeyBytes),
            IdentityAgreementKey = ByteString.CopyFrom(ECDiffieHellman.Create().PublicKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(ECDiffieHellman.Create().PublicKey.ExportSubjectPublicKeyInfo()),
            PreKeySignature = ByteString.CopyFrom(new byte[64]) // No longer needs to be valid
        };
        var response = new EstablishSessionResponse { ResponderBundle = responderBundle };
        var fakeCall = new AsyncUnaryCall<EstablishSessionResponse>(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

        mockTransportClient.Setup(c => c.EstablishSessionAsync(It.IsAny<EstablishSessionRequest>(), null, null, CancellationToken.None))
            .Returns(fakeCall);

        _mockTlsCertService.Setup(s => s.GetOrCreateTlsCertificateAsync(It.IsAny<string>(), It.IsAny<byte[]>()))
            .ReturnsAsync(new X509Certificate2());
        _mockGrpcFactory.Setup(f => f.CreateClient(endpoint, It.IsAny<X509Certificate2>(), It.IsAny<TlsCertificate?>()))
            .Returns(mockTransportClient.Object);

        // Mock other dependencies to allow the method to complete
        _mockLocalPeerProvider.Setup(p => p.GetPeerIdAsync()).Returns(Task.FromResult(new SessionPeerId(Guid.NewGuid())));

        // Act
        var conversationId = await _sut.CreateDirectConversationAsync(endpoint, peerName, null);

        // Assert
        Assert.That(conversationId.Value, Is.Not.EqualTo(Guid.Empty));
        _mockPeerRepo.Verify(r => r.AddAsync(It.Is<Peer>(p => p.Name == peerName)), Times.Once);
        _mockPeerConnectionRepo.Verify(r => r.SaveAsync(It.Is<PeerConnection>(pc =>
            pc.TlsCertificates.Count == 1 &&
            pc.TlsCertificates[0].Value.SequenceEqual(remoteIdentityKeyBytes) &&
            pc.GrpcEndPoints.Count == 1 &&
            pc.GrpcEndPoints[0].EndPoint.Equals(endpoint)
        )), Times.Once);
    }
}
