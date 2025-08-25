using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CryptoSharedSecret = Percolator.Cryptography.SharedSecret;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Percolator.Application.Configuration;
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
using IdentityPeerId = Percolator.Identity.PeerId;
using RatchetIdentityKey = Percolator.Cryptography.RatchetIdentityKey;
using RatchetEphemeralKey = Percolator.Cryptography.RatchetEphemeralKey;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class ConversationServiceTests
{
    private Mock<IPeerRepository> _mockPeerRepository;
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
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
            )
        };

        _service = new ConversationService(
            _testLogger,
            _mockX3dhOrchestrator.Object,
            _mockDirectSessionManager.Object,
            _mockConversationRepository.Object,
            _mockOneTimeKeyProvider.Object,
            _activeIdentityContext,
            _mockGrpcSessionService.Object, 
            new X3DHManager(NullLogger<X3DHManager>.Instance, Options.Create(new CryptographyOptions())), 
            _mockPeerConnectionRepository.Object,
            Options.Create(new TransportOptions { GrpcPort = 52382 })
        );
    }

    [Test]
    public async Task CreateDirectConversation_WhenPeerExists_EstablishesSessionAndCreatesConversation()
    {
        // Arrange
        var endpoint = new DnsEndPoint("localhost", 5001);
        var peer = new Peer(new IdentityPeerId(Guid.NewGuid()), "test-peer");

        // Setup peer repository to return the existing peer
        _mockPeerRepository.Setup(r => r.GetByNameAsync(peer.Name)).ReturnsAsync(peer);
        _mockPeerRepository.Setup(r => r.GetByIdAsync(It.Is<IdentityPeerId>(id => id.Value == peer.Id.Value))).ReturnsAsync(peer);

        // Create valid crypto materials for the mock response
        using var remoteSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var remoteEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responsePayload = new EstablishDirectSessionResponse.Types.ResponsePayload
        {
            EphemeralKey = ByteString.CopyFrom(remoteEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            SessionId = Guid.NewGuid().ToString(),
        };
        var responsePayloadBytes = responsePayload.ToByteString();
        var signature = remoteSigningKey.SignData(responsePayloadBytes.ToByteArray(), HashAlgorithmName.SHA256);
        
        var grpcResponse = new EstablishDirectSessionResponse
        {
            Response = new EstablishDirectSessionResponse.Types.Response
            {
                IdentitySigningKey = ByteString.CopyFrom(remoteSigningKey.ExportSubjectPublicKeyInfo()),
                ResponsePayload =  responsePayloadBytes,
                PayloadSignature = ByteString.CopyFrom(signature)
            }
        };
        _mockGrpcSessionService.Setup(s => s.EstablishDirectSessionAsync(It.IsAny<DnsEndPoint>(), It.IsAny<EstablishDirectSessionRequest>()))
            .ReturnsAsync(grpcResponse);
        
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
        var dummyBundle = new X3dPreKeyBundle
        (
            new RatchetIdentityKey(new byte[32]),
            new RatchetAgreementKey(new byte[32]),
            new PreKey(new byte[32]),
            new OneTimeKey(new byte[32])
        );
        var handshakeResponse = new HandshakeResponse(
            new CryptoSharedSecret(new byte[32]),
            dummyBundle,
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
        );
        _mockX3dhOrchestrator
            .Setup(o => o.CompleteHandshake(
                It.IsAny<RatchetIdentityKey>(), 
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<ECDiffieHellman>()))
            .Returns(handshakeResponse);

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
                It.IsAny<IdentityPeerId>(), 
                It.IsAny<RatchetIdentityKey>(), 
                It.IsAny<RatchetEphemeralKey>(), 
                It.IsAny<CryptoSharedSecret>(), 
                It.IsAny<ECDiffieHellman>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateNewDirectConversationAsync(endpoint, peer);

        // Assert
        Assert.That(result, Is.Not.EqualTo(default(ChatConversationId)));
        _mockConversationRepository.Verify(r => r.AddAsync(It.Is<ChatConversation>(c => c.Name == peer.Name)), Times.Once);
        _mockDirectSessionManager.Verify(m => m.EstablishSessionAsResponderAsync(
            It.IsAny<Percolator.Cryptography.SessionId>(), 
            It.IsAny<IdentityPeerId>(), 
            It.IsAny<RatchetIdentityKey>(), 
            It.IsAny<RatchetEphemeralKey>(),
            It.IsAny<ECDiffieHellman>(), 
            It.IsAny<CryptoSharedSecret>()), Times.Once);
        
        // Verify that our services were called correctly
        _mockTlsHandshakeService.Verify(s => s.CaptureCertificateAsync(It.IsAny<DnsEndPoint>()), Times.Never);
        _mockGrpcSessionService.Verify(s => s.EstablishDirectSessionAsync(It.IsAny<DnsEndPoint>(), It.IsAny<EstablishDirectSessionRequest>()), Times.Once);
    }

    [Test]
    public async Task CreateDirectConversation_WhenPeerDoesNotExist_CreatesNewPeerAndConversation()
    {
        // Arrange
        var endpoint = new DnsEndPoint("localhost", 5001);
        var peerName = "new-peer";
        var sessionId = Guid.Parse("43e97c9d-5d15-466f-8d0e-4ed1ab1bb7be");
        var peer = new Peer(new IdentityPeerId(Guid.NewGuid()), peerName);
        
        // Create valid crypto materials for the mock response
        using var remoteSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var remoteEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responsePayload = new EstablishDirectSessionResponse.Types.ResponsePayload
        {
            EphemeralKey = ByteString.CopyFrom(remoteEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            SessionId = sessionId.ToString(),
        };
        var responsePayloadBytes = responsePayload.ToByteString();
        var signature = remoteSigningKey.SignData(responsePayloadBytes.ToByteArray(), HashAlgorithmName.SHA256);

        var grpcResponse = new EstablishDirectSessionResponse
        {
            Response = new EstablishDirectSessionResponse.Types.Response
            {
                IdentitySigningKey = ByteString.CopyFrom(remoteSigningKey.ExportSubjectPublicKeyInfo()),
                ResponsePayload =  responsePayloadBytes,
                PayloadSignature = ByteString.CopyFrom(signature)
            }
        };
        _mockGrpcSessionService.Setup(s => s.EstablishDirectSessionAsync(It.IsAny<DnsEndPoint>(), It.IsAny<EstablishDirectSessionRequest>()))
            .ReturnsAsync(grpcResponse);

        // Setup peer connection repository
        _mockPeerConnectionRepository
            .Setup(r => r.SaveAsync(It.IsAny<PeerConnection>()))
            .Returns(Task.CompletedTask);

        // Setup X3DH orchestrator to return a shared secret
        var ratchetIdentityKey = new RatchetIdentityKey(new byte[32]);
        var dummyBundle = new X3dPreKeyBundle
        (
            ratchetIdentityKey,
            new RatchetAgreementKey(new byte[32]),
            new PreKey(new byte[32]),
            new OneTimeKey(new byte[32])
        );
        var handshakeResponse = new HandshakeResponse(
            new CryptoSharedSecret(new byte[32]),
            dummyBundle,
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
        );
        _mockX3dhOrchestrator
            .Setup(o => o.CompleteHandshake(
                It.IsAny<RatchetIdentityKey>(), 
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<ECDiffieHellman>()))
            .Returns(handshakeResponse);

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
                It.IsAny<CryptoSharedSecret>(), 
                It.IsAny<ECDiffieHellman>()))
            .Returns(Task.CompletedTask);

        // Capture the conversation that gets created
        ChatConversation capturedConversation = null;
        _mockConversationRepository.Setup(r => r.AddAsync(It.IsAny<ChatConversation>()))
            .Callback<ChatConversation>(c => capturedConversation = c)
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateNewDirectConversationAsync(endpoint, peer);
        
        // Assert
        Assert.That(result, Is.Not.EqualTo(default(ChatConversationId)));
        Assert.That(result.Value, Is.EqualTo(sessionId));
        
        // Verify peer connection was saved
        _mockPeerConnectionRepository.Verify(r => r.SaveAsync(It.IsAny<PeerConnection>()), Times.Once);
        
        // Verify conversation was created with expected participants
        _mockConversationRepository.Verify(r => r.AddAsync(It.IsAny<ChatConversation>()), Times.Once);
        Assert.That(capturedConversation, Is.Not.Null);
        Assert.That(capturedConversation.Id.Value, Is.EqualTo(sessionId));
        Assert.That(capturedConversation.Name, Is.EqualTo(peerName));
        Assert.That(capturedConversation.Participants, Has.Count.EqualTo(2));
        
        // Verify session was established
        _mockDirectSessionManager.Verify(m => m.EstablishSessionAsResponderAsync(
            It.Is<Percolator.Cryptography.SessionId>(id => id.Value == sessionId),
            It.IsAny<IdentityPeerId>(), 
            It.IsAny<RatchetIdentityKey>(), 
            It.IsAny<RatchetEphemeralKey>(), 
            It.IsAny<ECDiffieHellman>(),
            It.Is<CryptoSharedSecret>(s => s.Value.SequenceEqual(handshakeResponse.SharedSecret.Value))),
            Times.Once);
            
        // Verify gRPC service was called
        _mockGrpcSessionService.Verify(s => s.EstablishDirectSessionAsync(
            endpoint,
            It.IsAny<EstablishDirectSessionRequest>()),
            Times.Once);
    }

    [Test]
    public async Task CreateDirectConversation_WhenGrpcServiceThrows_PropagatesException()
    {
        // Arrange
        var endpoint = new DnsEndPoint("localhost", 5001);
        var peerName = "test-peer";
        var peer = new Peer(new IdentityPeerId(Guid.NewGuid()), peerName);
        
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
            .Setup(s => s.EstablishDirectSessionAsync(
                It.IsAny<DnsEndPoint>(),
                It.IsAny<EstablishDirectSessionRequest>()))
            .ThrowsAsync(expectedError);

        // Act & Assert
        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _service.CreateNewDirectConversationAsync(endpoint, peer));
            
        Assert.That(exception, Is.SameAs(expectedError));
        
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
            It.IsAny<CryptoSharedSecret>(), 
            It.IsAny<ECDiffieHellman>()),
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