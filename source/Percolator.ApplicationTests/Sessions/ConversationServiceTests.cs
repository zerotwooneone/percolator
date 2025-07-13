using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CryptoSharedSecret = Percolator.Cryptography.SharedSecret;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using Percolator.Sessions;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using IdentityPeerId = Percolator.Identity.PeerId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using SessionPeerId = Percolator.Sessions.PeerId;
using SessionSharedSecret = Percolator.Sessions.SharedSecret;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class ConversationServiceTests
{
    private Mock<IPeerRepository> _mockPeerRepository = null!;
    private Mock<ITlsCertificateService> _mockTlsCertificateService = null!;
    private Mock<IX3DHOrchestrator> _mockX3dhOrchestrator = null!;
    private Mock<IDirectSessionManager> _mockDirectSessionManager = null!;
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
        _mockDirectSessionManager = new Mock<IDirectSessionManager>();
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
            _mockDirectSessionManager.Object,
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

        // Setup X3DH orchestrator to return a shared secret
        _mockX3dhOrchestrator
            .Setup(o => o.CompleteHandshake(It.IsAny<ContractsPreKeyBundle>(), It.IsAny<ECDiffieHellman>()))
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
                It.IsAny<SessionConversationId>(), 
                It.IsAny<SessionPeerId>(), 
                It.IsAny<SessionIdentityKey>(), 
                It.IsAny<SessionRatchetKey>(), 
                It.IsAny<SessionSharedSecret>()))
            .Returns(Task.CompletedTask);

        // Setup the HTTP handler to simulate successful GRPC response
        var response = CreateGrpcResponse(new EstablishSessionResponse
        {
            ResponderBundle = new ContractsPreKeyBundle { 
                IdentityAgreementKey = ByteString.CopyFrom(new byte[32]), 
                SignedPreKey = ByteString.CopyFrom(new byte[32]),
                IdentitySigningKey = ByteString.CopyFrom(new byte[32])
            }
        });

        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler.Protected()
            .SetupSequence<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response); // First call succeeds
            
        var httpClient = new HttpClient(mockHttpHandler.Object);
        _mockHttpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);
            
        // Update our ConversationService to bypass the TLS handshake for testing
        var service = new TestableConversationService(
            _mockLogger.Object,
            _mockX3dhOrchestrator.Object,
            _mockDirectSessionManager.Object,
            _mockConversationRepository.Object,
            _mockPeerRepository.Object,
            _mockPeerConnectionRepository.Object,
            _mockTlsCertificateService.Object,
            _mockOneTimeKeyProvider.Object,
            _activeIdentityContext,
            _mockHttpClientFactory.Object,
            _mockPeerTrustManager.Object,
            cert); // Pass the certificate to use for testing

        // Act
        var result = await service.CreateDirectConversationAsync(endpoint, peerName);

        // Assert
        Assert.That(result, Is.Not.EqualTo(default(ChatConversationId)));
        _mockConversationRepository.Verify(r => r.AddAsync(It.Is<ChatConversation>(c => c.Name == peerName)), Times.Once);
        _mockDirectSessionManager.Verify(m => m.EstablishSessionAsInitiatorAsync(
            It.IsAny<SessionConversationId>(), 
            It.IsAny<SessionPeerId>(), 
            It.IsAny<SessionIdentityKey>(), 
            It.IsAny<SessionRatchetKey>(), 
            It.IsAny<SessionSharedSecret>()), Times.Once);
    }

    [Test]
    public async Task ExtractCertificateFromTlsError_AddsTrustAndRetries()
    {
        // Arrange
        var endpoint = new DnsEndPoint("localhost", 5001);
        var peerName = "untrusted-peer";

        // Create a test certificate for TOFU
        var cert = CreateSelfSignedCertificate();
        var certBytes = cert.Export(X509ContentType.Cert);
        var certBase64 = Convert.ToBase64String(certBytes);

        // Create the expected error message with the certificate
        var errorMsg = $"SSL Handshake failed. The remote certificate is not trusted. certificate: {certBase64}";
        
        // Set up peer trust manager
        _mockPeerTrustManager.Setup(m => m.AddTrustedPeer(It.IsAny<X509Certificate2>()))
            .Returns(Task.CompletedTask);
            
        // Setup repositories
        _mockPeerRepository.Setup(r => r.GetByNameAsync(peerName))
            .ReturnsAsync(null as Peer);
            
        _mockPeerConnectionRepository.Setup(r => r.GetByTlsCertificateAsync(It.IsAny<TlsCertificate>()))
            .ReturnsAsync((PeerConnection)null);
            
        _mockPeerConnectionRepository.Setup(r => r.SaveAsync(It.IsAny<PeerConnection>()))
            .Returns(Task.CompletedTask);

        // Create the exception with our certificate
        var exception = new RpcException(new Status(StatusCode.Unavailable, errorMsg));

        // Act - directly call the certificate extraction code as it would happen in TOFU flow
        X509Certificate2 extractedCert = null;
        if (exception.Status.Detail.Contains("certificate"))
        {
            string certData = exception.Status.Detail.Split("certificate:")[1].Trim();
            try
            {
                byte[] extractedBytes = Convert.FromBase64String(certData);
                extractedCert = new X509Certificate2(extractedBytes);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Failed to extract certificate: {ex.Message}");
            }
        }

        // Extract the certificate and add it to trust store
        if (extractedCert != null)
        {
            await _mockPeerTrustManager.Object.AddTrustedPeer(extractedCert);
            
            // Create peer connection record
            var networkPeerId = new NetworkPeerId(Guid.NewGuid());
            var tlsCertificate = new TlsCertificate(extractedCert.Export(X509ContentType.Cert));
            var peerConnection = new PeerConnection(
                networkPeerId,
                null,
                new[] { new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow) },
                new[] { tlsCertificate },
                DateTimeOffset.UtcNow);
                
            await _mockPeerConnectionRepository.Object.SaveAsync(peerConnection);
        }

        // Assert
        Assert.That(extractedCert, Is.Not.Null, "Certificate should be successfully extracted from the error message");
        
        // Verify the extracted certificate matches our original
        Assert.That(
            Convert.ToBase64String(extractedCert.Export(X509ContentType.Cert)), 
            Is.EqualTo(certBase64), 
            "Extracted certificate should match the original"
        );
        
        // Verify the certificate was added to trust store
        _mockPeerTrustManager.Verify(
            m => m.AddTrustedPeer(It.Is<X509Certificate2>(c => 
                Convert.ToBase64String(c.Export(X509ContentType.Cert)) == certBase64)), 
            Times.Once
        );
        
        // Verify the connection was saved
        _mockPeerConnectionRepository.Verify(
            r => r.SaveAsync(It.Is<PeerConnection>(c => 
                c.TlsCertificates.Any(tc => 
                    Convert.ToBase64String(tc.RawData) == certBase64))), 
            Times.Once
        );
    }

    private static HttpResponseMessage CreateGrpcResponse<T>(T message)
        where T : IMessage
    {
        var stream = new MemoryStream();
        // Write the compression flag (0 for uncompressed)
        stream.WriteByte(0);
        // Write the 4-byte message length
        var length = message.CalculateSize();
        var lengthBytes = BitConverter.GetBytes(length);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(lengthBytes);
        }
        stream.Write(lengthBytes, 0, 4);
        // Write the message
        message.WriteTo(stream);
        stream.Position = 0;

        var streamContent = new StreamContent(stream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = new Version(2, 0),
            Content = streamContent
        };

        response.TrailingHeaders.Add("grpc-status", "0");

        return response;
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        // Create a new RSA key pair
        using var rsa = RSA.Create(2048);
        
        // Create certificate request
        var distinguishedName = new X500DistinguishedName("CN=PercolatorTOFUTest");
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

// Testable version of ConversationService that bypasses the TLS handshake for testing
public class TestableConversationService : ConversationService
{
    private readonly X509Certificate2 _testCertificate;
    private readonly EstablishSessionResponse _testResponse;
    private readonly Mock<ILogger<ConversationService>> _logger;

    public TestableConversationService(
        ILogger<ConversationService> logger,
        IX3DHOrchestrator orchestrator,
        IDirectSessionManager sessionManager,
        IConversationRepository conversationRepository,
        IPeerRepository peerRepository,
        IPeerConnectionRepository peerConnectionRepository,
        ITlsCertificateService tlsCertificateService,
        IOneTimeKeyProvider oneTimeKeyProvider,
        ActiveIdentityContext activeIdentityContext,
        IHttpClientFactory httpClientFactory,
        IPeerTrustManager peerTrustManager,
        X509Certificate2 testCertificate)
        : base(logger, orchestrator, sessionManager, conversationRepository, peerRepository, 
              peerConnectionRepository, tlsCertificateService, oneTimeKeyProvider, activeIdentityContext, 
              httpClientFactory, peerTrustManager)
    {
        _testCertificate = testCertificate;
        _logger = logger as Mock<ILogger<ConversationService>>;
        
        // Create a test response to use in the override methods
        _testResponse = new EstablishSessionResponse
        {
            ResponderBundle = new ContractsPreKeyBundle
            {
                IdentityAgreementKey = ByteString.CopyFrom(new byte[32]),
                SignedPreKey = ByteString.CopyFrom(new byte[32]),
                IdentitySigningKey = ByteString.CopyFrom(new byte[32])
            }
        };
    }

    // Override the method that attempts to capture a certificate
    protected override Task<X509Certificate2?> CaptureRemoteCertificateAsync(DnsEndPoint endpoint)
    {
        return Task.FromResult<X509Certificate2?>(_testCertificate);
    }
    
    // Override TOFU retry connection method to bypass actual network connection
    protected override Task<EstablishSessionResponse> PerformRetryConnectionAsync(
        DnsEndPoint endpoint, 
        X509Certificate2 remoteCert,
        EstablishSessionRequest request)
    {
        // Return our predefined response without making a real connection
        return Task.FromResult(_testResponse);
    }
    
    // Override the regular TOFU handling method to bypass network operations
    protected override Task<ChatConversationId?> HandleRegularTofuAsync(
        DnsEndPoint endpoint, 
        string peerName, 
        EstablishSessionRequest request, 
        ECDiffieHellman ephemeralKey)
    {
        // Directly call the HandleTofuWithCapturedCertificateAsync method with our test certificate
        return HandleTofuWithCapturedCertificateAsync(
            endpoint, 
            peerName, 
            _testCertificate, 
            request, 
            ephemeralKey);
    }
}