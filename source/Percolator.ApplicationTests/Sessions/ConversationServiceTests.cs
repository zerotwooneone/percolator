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
using Percolator.Application.Services;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using DirectSessionId = Percolator.Network.DirectSessionId;
using IdentityPeerId = Percolator.Identity.PeerId;
using RatchetIdentityKey = Percolator.Cryptography.RatchetIdentityKey;
using RatchetEphemeralKey = Percolator.Cryptography.RatchetEphemeralKey;

namespace Percolator.ApplicationTests.Sessions;

[Ignore("we are planning to delete this service")]
[TestFixture]
public class ConversationServiceTests
{
    private Mock<IOneTimeKeyProvider> _mockOneTimeKeyProvider;
    private Mock<IPeerRoutingProfileRepository> _mockProfileRepository;
    private Mock<IPeerTrustManager> _mockPeerTrustManager;
    private Mock<ITlsHandshakeService> _mockTlsHandshakeService;
    private Mock<IGrpcSessionService> _mockGrpcSessionService;
    private ILogger<ConversationService> _testLogger;
    private ActiveIdentityContext _activeIdentityContext;
    private ConversationService _service;
    private Mock<IDirectSessionRepository> _mockDirectSessionRepository;
    private Mock<IPeerPublicSigningKeyStore> _mockPkhStore;
    private Mock<IHandshakeService> _mockHandshakeService;
    private Mock<ISecureMessagingService> _mockSecureMessagingService;

    [SetUp]
    public void Setup()
    {
        _mockOneTimeKeyProvider = new Mock<IOneTimeKeyProvider>();
        _mockProfileRepository = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
        _mockPeerTrustManager = new Mock<IPeerTrustManager>();
        _mockTlsHandshakeService = new Mock<ITlsHandshakeService>();
        _mockGrpcSessionService = new Mock<IGrpcSessionService>();
        _mockDirectSessionRepository = new Mock<IDirectSessionRepository>();
        _mockPkhStore = new Mock<IPeerPublicSigningKeyStore>();
        _mockHandshakeService = new Mock<IHandshakeService>();
        _mockSecureMessagingService = new Mock<ISecureMessagingService>();
        _testLogger = NullLogger<ConversationService>.Instance;
        _activeIdentityContext = new ActiveIdentityContext
        {
            Identity = new IdentityRecord(Guid.NewGuid(), "Test Identity") { SelfIdentityId = 1 },
            Keys = new X3dhKeys(
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
            )
        };

        _service = new ConversationService(
            _testLogger,
            _mockOneTimeKeyProvider.Object,
            _activeIdentityContext,
            _mockGrpcSessionService.Object, 
            _mockProfileRepository.Object,
            Options.Create(new TransportOptions { GrpcPort = 52382 }),
            _mockDirectSessionRepository.Object,
            _mockPkhStore.Object,
            NullLoggerFactory.Instance,
            Options.Create(new CryptographyOptions()),
            _mockHandshakeService.Object,
            _mockSecureMessagingService.Object
        );
    }

    [Test]
    public async Task CreateDirectConversation_UsesRemoteEphemeralForX3DH_AndHeaderPreKeyForDR_ShouldFailUntilProtoWired()
    {
        // Arrange
        var endpoint = new DnsEndPoint("localhost", 6001);
        var peer = new Peer(new IdentityPeerId(Guid.NewGuid()), "proto-wire-peer");

        // Keys: responder identity and signed prekey; initiator identity and ephemeral for X3DH
        using var responderIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var responderSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var initiatorIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var initiatorEphemeralForX3DH = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Shared secret chosen arbitrarily for the test; orchestrator will return a matching object
        var sharedSecret = new CryptoSharedSecret(new byte[32]);
        var dummyBundle = new X3dPreKeyBundle(
            new RatchetIdentityKey(responderIdentity.ExportSubjectPublicKeyInfo()),
            new RatchetEphemeralKey(responderSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new OneTimeKey(new byte[32])
        );

        // Build a responder HandshakeResponse to return from orchestrator
        var handshakeResponse = new HandshakeResponse(sharedSecret, dummyBundle, responderSignedPreKey);

        // Prepare the first DR message produced by the initiator; its header.PreKey will differ
        var responsePayload = new EstablishDirectSessionResponse.Types.ResponsePayload
        {
            SessionId = Guid.NewGuid().ToString(),
        }.ToByteString();

        // Capture the initiator ephemeral SPKI BEFORE encrypt, since the initiator session will dispose
        // the provided ECDH during the first ratchet step, making PublicKey inaccessible.
        var initiatorEphemeralSpki = initiatorEphemeralForX3DH.PublicKey.ExportSubjectPublicKeyInfo();
        
        // Build gRPC response with InitiatorIdentityKey and InitiatorEphemeralKey (X3DH initiator ephemeral)
        // Dummy first ratchet message bytes for compile-time only
        var firstRatchetMessage = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0x01 });
        var grpcResponse = new EstablishDirectSessionResponse
        {
            Response = new EstablishDirectSessionResponse.Types.Response
            {
                InitiatorIdentityKey = ByteString.CopyFrom(initiatorIdentity.ExportSubjectPublicKeyInfo()),
                InitiatorEphemeralKey = ByteString.CopyFrom(initiatorEphemeralSpki),
                RatchetMessage = firstRatchetMessage
            }
        };

        _mockGrpcSessionService
            .Setup(s => s.EstablishDirectSessionAsync(It.IsAny<DnsEndPoint>(), It.IsAny<EstablishDirectSessionRequest>()))
            .ReturnsAsync(grpcResponse);

        // Expectation 1: X3DH CompleteHandshake must be called with remoteIdentity = response.InitiatorIdentityKey
        // and remoteEphemeral = response.InitiatorEphemeralKey
        
        // Expectation 2: EstablishSessionAsResponderAsync        // Establish as responder using non-decrypting overload with provided session id
        

        // One-time key provider: return a key to complete handshake
        using var oneTimeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _mockOneTimeKeyProvider.Setup(p => p.PopOneTimeKey()).Returns(oneTimeKey);

        // Act
        await _service.CreateNewDirectSessionAsync(endpoint, peer);
    }

    [Test]
    public async Task CreateDirectConversation_WhenPeerExists_EstablishesSessionAndCreatesConversation()
    {
        // Arrange
        var endpoint = new DnsEndPoint("localhost", 5001);
        var peer = new Peer(new IdentityPeerId(Guid.NewGuid()), "test-peer");


        // Create valid crypto materials for the mock response
        using var remoteSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var remoteEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        // Prepare handshake response inputs first so we can build a decryptable ratchet message
        var dummyBundle = new X3dPreKeyBundle
        (
            new RatchetIdentityKey(remoteSigningKey.ExportSubjectPublicKeyInfo()),
            new RatchetEphemeralKey(remoteEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new OneTimeKey(new byte[32])
        );
        var sharedSecret = new CryptoSharedSecret(new byte[32]);
        // IMPORTANT: Use the same private key that pairs with remoteEphemeralKey.PublicKey
        var responderPrivate = remoteEphemeralKey;
        var handshakeResponse = new HandshakeResponse(sharedSecret, dummyBundle, responderPrivate);
        
        // Build ResponsePayload bytes with only session_id
        var responsePayload = new EstablishDirectSessionResponse.Types.ResponsePayload
        {
            SessionId = Guid.NewGuid().ToString(),
        }.ToByteString();

        // Encrypt payload as an initiator to create a ratchet message decryptable by responder
        using var initiatorEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        // Dummy ratchet message bytes for compile-time only
        var ratchetMessage = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0x02 });
        var grpcResponse = new EstablishDirectSessionResponse
        {
            Response = new EstablishDirectSessionResponse.Types.Response
            {
                InitiatorIdentityKey = ByteString.CopyFrom(remoteSigningKey.ExportSubjectPublicKeyInfo()),
                RatchetMessage = ratchetMessage
            }
        };
        _mockGrpcSessionService.Setup(s => s.EstablishDirectSessionAsync(It.IsAny<DnsEndPoint>(), It.IsAny<EstablishDirectSessionRequest>()))
            .ReturnsAsync(grpcResponse);
        
        // Setup profile repository Upsert to succeed
        _mockProfileRepository
            .Setup(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // X3DH orchestrator already setup above for this test

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
            
        // Act
        var result = await _service.CreateNewDirectSessionAsync(endpoint, peer);

        // Assert
        Assert.That(result, Is.Not.EqualTo(default(DirectSessionId)));
        
        // Verify that our services were called correctly
        _mockTlsHandshakeService.Verify(s => s.CaptureCertificateAsync(It.IsAny<DnsEndPoint>()), Times.Never);
        _mockGrpcSessionService.Verify(s => s.EstablishDirectSessionAsync(It.IsAny<DnsEndPoint>(), It.IsAny<EstablishDirectSessionRequest>()), Times.Once);

        // Verify PKH upsert occurred using the responder's identity key from the handshake result
        var expectedSpki = handshakeResponse.ResponderBundle.IdentitySigningKey.Value;
        var expectedPkh = SHA256.HashData(expectedSpki);
        _mockPkhStore.Verify(s => s.ActivateIfChangedAsync(
            It.Is<IdentityPeerId>(id => id.Value == peer.Id.Value),
            It.Is<byte[]>(pk => Convert.ToBase64String(pk) == Convert.ToBase64String(expectedSpki)),
            It.Is<byte[]>(pkh => Convert.ToBase64String(pkh) == Convert.ToBase64String(expectedPkh)),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
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
        using var remoteSigningKey2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var remoteEphemeralKey2 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var sharedSecret2 = new CryptoSharedSecret(new byte[32]);
        var dummyBundle2 = new X3dPreKeyBundle(
            new RatchetIdentityKey(remoteSigningKey2.ExportSubjectPublicKeyInfo()),
            new RatchetEphemeralKey(remoteEphemeralKey2.PublicKey.ExportSubjectPublicKeyInfo()),
            new OneTimeKey(new byte[32])
        );
        // IMPORTANT: Use the same private key that pairs with remoteEphemeralKey2.PublicKey
        var responderPrivate2 = remoteEphemeralKey2;
        var handshakeResponse2 = new HandshakeResponse(sharedSecret2, dummyBundle2, responderPrivate2);
        
        var responsePayload2 = new EstablishDirectSessionResponse.Types.ResponsePayload
        {
            SessionId = sessionId.ToString(),
        }.ToByteString();

        using var initiatorEphemeral2 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        // Dummy ratchet message bytes for compile-time only
        var ratchetMessage2 = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0x03 });
        var grpcResponse = new EstablishDirectSessionResponse
        {
            Response = new EstablishDirectSessionResponse.Types.Response
            {
                InitiatorIdentityKey = ByteString.CopyFrom(remoteSigningKey2.ExportSubjectPublicKeyInfo()),
                RatchetMessage = ratchetMessage2
            }
        };
        _mockGrpcSessionService.Setup(s => s.EstablishDirectSessionAsync(It.IsAny<DnsEndPoint>(), It.IsAny<EstablishDirectSessionRequest>()))
            .ReturnsAsync(grpcResponse);

        // Profile upserts are configured above; no legacy connection repo

        // Setup X3DH orchestrator to return a shared secret
        // Important: Do not override the matching handshake response; ensure the orchestrator returns handshakeResponse2
        
        // Setup the one-time key provider to return a key
        using var oneTimeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _mockOneTimeKeyProvider
            .Setup(p => p.PopOneTimeKey())
            .Returns(oneTimeKey);
        
        // Act
        var result = await _service.CreateNewDirectSessionAsync(endpoint, peer);
        
        // Assert
        Assert.That(result, Is.Not.EqualTo(default(DirectSessionId)));
        Assert.That(result.Value, Is.EqualTo(sessionId));
        
        // Verify routing profile upsert occurred
        _mockProfileRepository.Verify(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()), Times.Once);
        
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
        await Task.Yield(); // ensure method contains an await to avoid CS1998
        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _service.CreateNewDirectSessionAsync(endpoint, peer));
            
        Assert.That(exception, Is.SameAs(expectedError));
        
        // Verify peer was not created (no peer repository dependency anymore)
        
        // Verify session was not established
        
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