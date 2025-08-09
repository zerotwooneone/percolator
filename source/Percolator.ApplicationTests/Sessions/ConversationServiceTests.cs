using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CryptoSharedSecret = Percolator.Cryptography.SharedSecret;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
using NetworkPeerId = Percolator.Network.PeerId;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using IdentityPeerId = Percolator.Identity.PeerId;
using RatchetIdentityKey = Percolator.Cryptography.RatchetIdentityKey;
using RatchetEphemeralKey = Percolator.Cryptography.RatchetEphemeralKey;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class ConversationServiceTests
{
    private Mock<IPeerRepository> _mockPeerRepository;
    private Mock<ITlsCertificateService> _mockTlsCertificateService;
    private Mock<IX3DHOrchestrator> _mockX3dhOrchestrator;
    private Mock<IDirectSessionManager> _mockDirectSessionManager;
    private Mock<IOneTimeKeyProvider> _mockOneTimeKeyProvider;
    private Mock<IConversationRepository> _mockConversationRepository;
    private Mock<IPeerConnectionRepository> _mockPeerConnectionRepository;
    private Mock<IPeerTrustManager> _mockPeerTrustManager;
    private Mock<ITlsHandshakeService> _mockTlsHandshakeService;
    private Mock<IGrpcSessionService> _mockGrpcSessionService;
    private ILogger<ConversationService> _testLogger;
    private ActiveIdentityContext _activeIdentityContext;
    private ConversationService _service;

    [SetUp]
    public void Setup()
    {
        _mockPeerRepository = new Mock<IPeerRepository>();
        _mockTlsCertificateService = new Mock<ITlsCertificateService>();
        _mockX3dhOrchestrator = new Mock<IX3DHOrchestrator>();
        _mockDirectSessionManager = new Mock<IDirectSessionManager>();
        _mockOneTimeKeyProvider = new Mock<IOneTimeKeyProvider>();
        _mockConversationRepository = new Mock<IConversationRepository>();
        _mockPeerConnectionRepository = new Mock<IPeerConnectionRepository>();
        _mockPeerTrustManager = new Mock<IPeerTrustManager>();
        _mockTlsHandshakeService = new Mock<ITlsHandshakeService>();
        _mockGrpcSessionService = new Mock<IGrpcSessionService>();
        _testLogger = NullLogger<ConversationService>.Instance;
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
            _testLogger,
            _mockX3dhOrchestrator.Object,
            _mockDirectSessionManager.Object,
            _mockConversationRepository.Object,
            _mockPeerRepository.Object,
            _mockOneTimeKeyProvider.Object,
            _activeIdentityContext,
            _mockGrpcSessionService.Object, 
            new X3DHManager(NullLogger<X3DHManager>.Instance, new CryptographyOptions()), 
            _mockPeerConnectionRepository.Object
        );
    }

    [Test]
    public async Task CreateDirectConversationAsync_WhenPeerExists_EstablishesSessionAndCreatesConversation()
    {
        // Arrange
        var endpoint = new DnsEndPoint("localhost", 5001);
        var peerName = "test-peer";
        var peer = new Peer(new IdentityPeerId(Guid.NewGuid()), peerName);

        // Setup peer repository to return the existing peer
        _mockPeerRepository.Setup(r => r.GetByNameAsync(peerName)).ReturnsAsync(peer);
        _mockPeerRepository.Setup(r => r.GetByIdAsync(It.Is<IdentityPeerId>(id => id.Value == peer.Id.Value))).ReturnsAsync(peer);
        
        // Create a mock peer connection
        var peerConnection = new PeerConnection(
            new NetworkPeerId(peer.Id.Value),
            new DirectMessagePublicKey(new byte[32]), // Valid DirectMessagePublicKey
            new[] { new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow) },
            new[] { new TlsCertificate(new byte[100]) }, // Mock certificate
            DateTimeOffset.UtcNow
        );
        
        // Setup peer connection repository to return the connection when asked
        _mockPeerConnectionRepository
            .Setup(r => r.GetByTlsCertificateAsync(It.IsAny<TlsCertificate>()))
            .ReturnsAsync(peerConnection);
        
        _mockPeerConnectionRepository
            .Setup(r => r.UpdateDirectMessagePublicKeyAsync(It.IsAny<NetworkPeerId>(), It.IsAny<DirectMessagePublicKey>()))
            .Returns(Task.CompletedTask);

        _mockPeerConnectionRepository
            .Setup(r => r.SaveAsync(It.IsAny<PeerConnection>()))
            .Returns(Task.CompletedTask);

        // Setup X3DH orchestrator to return a shared secret
        _mockX3dhOrchestrator
            .Setup(o => o.InitiateHandshake(It.IsAny<ContractsPreKeyBundle>(), It.IsAny<ECDiffieHellman>()))
            .Returns(new CryptoSharedSecret(new byte[32]));

        // Setup the one-time key provider to return a key
        var oneTimeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _mockOneTimeKeyProvider
            .Setup(p => p.PopOneTimeKey())
            .Returns(oneTimeKey);
        
        // Setup peer trust manager
        var cert = CreateSelfSignedCertificate("localhost");
        _mockPeerTrustManager
            .Setup(m => m.AddTrustedPeer(It.IsAny<X509Certificate2>()))
            .Returns(Task.CompletedTask);
            
        // Setup conversation repository
        _mockConversationRepository
            .Setup(r => r.AddAsync(It.IsAny<ChatConversation>()))
            .Returns(Task.CompletedTask);
            
        // Setup direct session manager
        _mockDirectSessionManager
            .Setup(m => m.EstablishSessionAsInitiatorAsync(
                It.IsAny<Percolator.Cryptography.SessionId>(), 
                It.IsAny<Percolator.Identity.PeerId>(), 
                It.IsAny<RatchetIdentityKey>(), 
                It.IsAny<RatchetEphemeralKey>(), 
                It.IsAny<CryptoSharedSecret>()))
            .Returns(Task.CompletedTask);

        // Create the response for the GrpcSessionService
        var grpcResponse = new EstablishSessionResponse
        {
            ResponderBundle = new ContractsPreKeyBundle { 
                IdentityAgreementKey = ByteString.CopyFrom(new byte[32]), 
                SignedPreKey = ByteString.CopyFrom(new byte[32]),
                IdentitySigningKey = ByteString.CopyFrom(new byte[32])
            },
            SessionId = Guid.NewGuid().ToString() // Add a valid session ID as a GUID string
        };

        // Configure mocks for shared certificate approach
        var sharedCert = CreateSelfSignedCertificate("localhost");
        _mockTlsCertificateService
            .Setup(s => s.GetOrCreateTlsCertificateAsync(It.IsAny<string>(), It.IsAny<byte[]>()))
            .ReturnsAsync(sharedCert);
            
        // Setup GrpcSessionService to succeed with the shared certificate
        _mockGrpcSessionService
            .Setup(s => s.EstablishSessionAsync(
                It.IsAny<DnsEndPoint>(), 
                It.IsAny<EstablishSessionRequest>(),
                It.IsAny<X509Certificate2>()))
            .ReturnsAsync(grpcResponse);

        // Act
        var result = await _service.CreateDirectConversationAsync(endpoint, peerName);

        // Assert
        Assert.That(result, Is.Not.EqualTo(default(ChatConversationId)));
        _mockConversationRepository.Verify(r => r.AddAsync(It.Is<ChatConversation>(c => c.Name == peerName)), Times.Once);
        _mockDirectSessionManager.Verify(m => m.EstablishSessionAsInitiatorAsync(
            It.IsAny<Percolator.Cryptography.SessionId>(), 
            It.IsAny<Percolator.Identity.PeerId>(), 
            It.IsAny<RatchetIdentityKey>(), 
            It.IsAny<RatchetEphemeralKey>(), 
            It.IsAny<CryptoSharedSecret>()), Times.Once);
        
        // Verify that our services were called correctly
        _mockTlsHandshakeService.Verify(s => s.CaptureCertificateAsync(It.IsAny<DnsEndPoint>()), Times.Never);
        _mockGrpcSessionService.Verify(s => s.EstablishSessionAsync(
            It.IsAny<DnsEndPoint>(), 
            It.IsAny<EstablishSessionRequest>(),
            It.IsAny<X509Certificate2>()), 
            Times.Once);
    }

    [Test]
    public async Task CreateDirectConversationAsync_WhenPeerDoesNotExist_CreatesNewPeerAndConversation()
    {
        // Arrange
        var endpoint = new DnsEndPoint("localhost", 5001);
        var peerName = "new-peer";
        
        // Setup peer repository to return null (peer does not exist)
        _mockPeerRepository.Setup(r => r.GetByNameAsync(peerName)).ReturnsAsync((Peer)null);
        
        // Setup peer repository AddAsync to succeed
        _mockPeerRepository.Setup(r => r.AddAsync(It.IsAny<Peer>()))
            .Returns(Task.CompletedTask);
            
        // Setup peer connection repository
        _mockPeerConnectionRepository
            .Setup(r => r.SaveAsync(It.IsAny<PeerConnection>()))
            .Returns(Task.CompletedTask);

        // Setup X3DH orchestrator to return a shared secret
        var sharedSecret = new CryptoSharedSecret(new byte[32]);
        Random.Shared.NextBytes(sharedSecret.Value);
        
        _mockX3dhOrchestrator
            .Setup(o => o.InitiateHandshake(It.IsAny<ContractsPreKeyBundle>(), It.IsAny<ECDiffieHellman>()))
            .Returns(sharedSecret);

        // Setup the one-time key provider to return a key
        using var oneTimeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _mockOneTimeKeyProvider
            .Setup(p => p.PopOneTimeKey())
            .Returns(oneTimeKey);
            
        // Setup conversation repository
        _mockConversationRepository
            .Setup(r => r.AddAsync(It.IsAny<ChatConversation>()))
            .Returns(Task.CompletedTask);
            
        // Setup direct session manager
        _mockDirectSessionManager
            .Setup(m => m.EstablishSessionAsInitiatorAsync(
                It.IsAny<Percolator.Cryptography.SessionId>(), 
                It.IsAny<IdentityPeerId>(), 
                It.IsAny<RatchetIdentityKey>(), 
                It.IsAny<RatchetEphemeralKey>(), 
                It.IsAny<CryptoSharedSecret>()))
            .Returns(Task.CompletedTask);

        // Create the response for the GrpcSessionService
        var sessionId = Guid.NewGuid();
        var responderBundle = new ContractsPreKeyBundle { 
            IdentityAgreementKey = ByteString.CopyFrom(new byte[32]), 
            SignedPreKey = ByteString.CopyFrom(new byte[32]),
            IdentitySigningKey = ByteString.CopyFrom(new byte[32])
        };
        
        var grpcResponse = new EstablishSessionResponse
        {
            ResponderBundle = responderBundle,
            SessionId = sessionId.ToString()
        };

        // Setup GrpcSessionService to succeed
        _mockGrpcSessionService
            .Setup(s => s.EstablishSessionAsync(
                It.IsAny<DnsEndPoint>(), 
                It.IsAny<EstablishSessionRequest>(),
                It.IsAny<X509Certificate2>()))
            .ReturnsAsync(grpcResponse);
            
        // Capture the peer that gets created
        Peer capturedPeer = null;
        _mockPeerRepository.Setup(r => r.AddAsync(It.IsAny<Peer>()))
            .Callback<Peer>(p => capturedPeer = p)
            .Returns(Task.CompletedTask);
            
        // Capture the conversation that gets created
        ChatConversation capturedConversation = null;
        _mockConversationRepository.Setup(r => r.AddAsync(It.IsAny<ChatConversation>()))
            .Callback<ChatConversation>(c => capturedConversation = c)
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateDirectConversationAsync(endpoint, peerName);

        // Assert
        Assert.That(result, Is.Not.EqualTo(default(ChatConversationId)));
        Assert.That(result.Value, Is.EqualTo(sessionId));
        
        // Verify peer was created with expected name
        _mockPeerRepository.Verify(r => r.AddAsync(It.Is<Peer>(p => p.Name == peerName)), Times.Once);
        Assert.That(capturedPeer, Is.Not.Null);
        Assert.That(capturedPeer.Name, Is.EqualTo(peerName));
        
        // Verify peer connection was saved
        _mockPeerConnectionRepository.Verify(r => r.SaveAsync(It.IsAny<PeerConnection>()), Times.Once);
        
        // Verify conversation was created with expected participants
        _mockConversationRepository.Verify(r => r.AddAsync(It.IsAny<ChatConversation>()), Times.Once);
        Assert.That(capturedConversation, Is.Not.Null);
        Assert.That(capturedConversation.Id.Value, Is.EqualTo(sessionId));
        Assert.That(capturedConversation.Name, Is.EqualTo(peerName));
        Assert.That(capturedConversation.Participants, Has.Count.EqualTo(2));
        
        // Verify session was established
        _mockDirectSessionManager.Verify(m => m.EstablishSessionAsInitiatorAsync(
            It.Is<Percolator.Cryptography.SessionId>(id => id.Value == sessionId),
            It.IsAny<IdentityPeerId>(), 
            It.IsAny<RatchetIdentityKey>(), 
            It.IsAny<RatchetEphemeralKey>(), 
            It.Is<CryptoSharedSecret>(s => s.Value.SequenceEqual(sharedSecret.Value))), 
            Times.Once);
            
        // Verify gRPC service was called
        _mockGrpcSessionService.Verify(s => s.EstablishSessionAsync(
            endpoint,
            It.IsAny<EstablishSessionRequest>(),
            It.IsAny<X509Certificate2>()),
            Times.Once);
    }

    [Test]
    public async Task CreateDirectConversationAsync_WhenGrpcServiceThrows_PropagatesException()
    {
        // Arrange
        var endpoint = new DnsEndPoint("localhost", 5001);
        var peerName = "test-peer";
        
        // Setup peer repository
        _mockPeerRepository.Setup(r => r.GetByNameAsync(peerName))
            .ReturnsAsync((Peer)null);
            
        // Setup one-time key provider
        using var oneTimeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _mockOneTimeKeyProvider
            .Setup(p => p.PopOneTimeKey())
            .Returns(oneTimeKey);
            
        // Setup GrpcSessionService to throw
        var expectedError = new InvalidOperationException("Cannot connect to peer");
        _mockGrpcSessionService
            .Setup(s => s.EstablishSessionAsync(
                It.IsAny<DnsEndPoint>(),
                It.IsAny<EstablishSessionRequest>(),
                It.IsAny<X509Certificate2>()))
            .ThrowsAsync(expectedError);

        // Act & Assert
        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _service.CreateDirectConversationAsync(endpoint, peerName));
            
        Assert.That(exception.Message, Is.EqualTo("Failed to establish secure connection using shared certificate"));
        Assert.That(exception.InnerException, Is.SameAs(expectedError));
        
        // Verify peer was not created
        _mockPeerRepository.Verify(r => r.AddAsync(It.IsAny<Peer>()), Times.Never);
        
        // Verify conversation was not created
        _mockConversationRepository.Verify(r => r.AddAsync(It.IsAny<ChatConversation>()), Times.Never);
        
        // Verify session was not established
        _mockDirectSessionManager.Verify(m => m.EstablishSessionAsInitiatorAsync(
            It.IsAny<Percolator.Cryptography.SessionId>(),
            It.IsAny<IdentityPeerId>(),
            It.IsAny<RatchetIdentityKey>(),
            It.IsAny<RatchetEphemeralKey>(),
            It.IsAny<CryptoSharedSecret>()),
            Times.Never);
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string subjectName)
    {
        // Create a new RSA key pair
        using var rsa = RSA.Create(2048);
        
        // Create certificate request
        var distinguishedName = new X500DistinguishedName($"CN={subjectName}");
        var request = new CertificateRequest(distinguishedName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        
        // Add basic constraints extension
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        
        // Add key usage extension
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        
        // Add extended key usage extension
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
            false));
        
        // Create certificate with 1 year validity
        var notBefore = DateTime.Now;
        var notAfter = notBefore.AddYears(1);
        
        // Create self-signed certificate
        var certificate = request.CreateSelfSigned(notBefore, notAfter);
        
        return certificate;
    }
}