using FluentAssertions;
using Moq;
using Percolator.Application.KeyExchange;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using System.Security.Cryptography;
using Contracts = Percolator.Contracts;
using SessionPeerId = Percolator.Sessions.PeerId;
using CryptographyPreKeyBundle = Percolator.Cryptography.PreKeyBundle;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Percolator.Sessions;
using PublicKey = Percolator.Cryptography.PublicKey;

namespace Percolator.ApplicationTests.KeyExchange;

public class X3DHOrchestratorTests
{
    private Mock<X3DHManager> _mockX3dhManager = null!;
    private Mock<ActiveIdentityContext> _mockActiveIdentityContext = null!;
    private X3DHOrchestrator _orchestrator = null!;

    // Local keys
    private ECDsa _localIdentitySigningKey = null!;
    private ECDiffieHellman _localIdentityAgreementKey = null!;
    private ECDiffieHellman _localSignedPreKey = null!;
    private ECDiffieHellman _localOneTimePreKey = null!;
    private X509Certificate2 _localCertificate = null!;

    // Remote keys
    private ECDsa _remoteIdentitySigningKey = null!;
    private ECDiffieHellman _remoteIdentityAgreementKey = null!;
    private ECDiffieHellman _remoteSignedPreKey = null!;
    private ECDiffieHellman _remoteOneTimePreKey = null!;


    [SetUp]
    public void Setup()
    {
        _mockX3dhManager = new Mock<X3DHManager>();
        _mockActiveIdentityContext = new Mock<ActiveIdentityContext>();

        // Local keys setup
        _localIdentitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _localIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _localSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _localOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        // Create a self-signed certificate for the local identity
        var certRequest = new CertificateRequest("cn=test", _localIdentitySigningKey, HashAlgorithmName.SHA256);
        _localCertificate = certRequest.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
        
        // Remote keys setup
        _remoteIdentitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _remoteIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _remoteSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _remoteOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        // Mock ActiveIdentityContext
        _mockActiveIdentityContext.Setup(c => c.Certificate).Returns(_localCertificate);
        _mockActiveIdentityContext.Setup(c => c.X3dhKeys).Returns(new Percolator.Identity.X3dhKeys(
            _localIdentitySigningKey,
            _localIdentityAgreementKey,
            _localSignedPreKey,
            _localOneTimePreKey
        ));

        _orchestrator = new X3DHOrchestrator(_mockActiveIdentityContext.Object, _mockX3dhManager.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _localIdentitySigningKey.Dispose();
        _localIdentityAgreementKey.Dispose();
        _localSignedPreKey.Dispose();
        _localOneTimePreKey.Dispose();
        _localCertificate.Dispose();
        _remoteIdentitySigningKey.Dispose();
        _remoteIdentityAgreementKey.Dispose();
        _remoteSignedPreKey.Dispose();
        _remoteOneTimePreKey.Dispose();
    }


    [Test]
    public void InitiateHandshake_WithValidBundle_ReturnsCorrectResult()
    {
        // Arrange
        var remotePeerId = new SessionPeerId(Guid.NewGuid());
        var remoteSignedPreKeyBytes = _remoteSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var remoteBundle = new Contracts.PreKeyBundle
        {
            IdentityKey = ByteString.CopyFrom(_remoteIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(remoteSignedPreKeyBytes),
            PreKeySignature = ByteString.CopyFrom(_remoteIdentitySigningKey.SignData(remoteSignedPreKeyBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            OneTimePreKey = ByteString.CopyFrom(_remoteOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo())
        };

        var expectedSharedSecret = new SharedSecret(new byte[32]);
        var ephemeralKey = new PublicKey(new byte[65]);
        var handshakeResult = new HandshakeInitiationResult(expectedSharedSecret, ephemeralKey);
        
        _mockX3dhManager.Setup(m => m.InitiateHandshake(
                It.IsAny<CryptographyPreKeyBundle>(), 
                It.IsAny<ECDsa>(), 
                It.IsAny<ECDiffieHellman>()))
            .Returns(handshakeResult);

        // Act
        var result = _orchestrator.InitiateHandshake(remotePeerId, remoteBundle);

        // Assert
        result.Should().NotBeNull();
        result.SharedSecret.Should().Be(expectedSharedSecret);
        result.InitialRatchetPublicKey.Should().NotBeNull();
        result.LocalPreKeyBundle.Should().NotBeNull();
        result.LocalPreKeyBundle.PreKeySignature.Should().NotBeEmpty();
    }

    [Test]
    public void ProcessHandshake_WithValidData_ReturnsCorrectResult()
    {
        // Arrange
        var remotePeerId = new SessionPeerId(Guid.NewGuid());
        var remoteEphemeralPublicKey = _remoteOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var localPreKeyBundle = new Contracts.PreKeyBundle(); // This is sent by initiator, so it's "local" from orchestrator's PoV
        
        var expectedSharedSecret = new SharedSecret(new byte[32]);
        _mockX3dhManager.Setup(m => m.RespondToHandshake(
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<ECDsa>(),
                It.IsAny<ECDiffieHellman>(),
                It.IsAny<ECDiffieHellman>(),
                It.IsAny<ECDiffieHellman>()))
            .Returns(expectedSharedSecret);

        // Act
        var result = _orchestrator.ProcessHandshake(remotePeerId, localPreKeyBundle, remoteEphemeralPublicKey);

        // Assert
        result.Should().NotBeNull();
        result.SharedSecret.Should().Be(expectedSharedSecret);
        result.InitialRatchetPublicKey.Should().NotBeNull();
    }
}
