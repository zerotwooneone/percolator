using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using CryptoPreKeyBundle = Percolator.Cryptography.PreKeyBundle;
using SessionPeerId = Percolator.Sessions.PeerId;

namespace Percolator.ApplicationTests.KeyExchange;

public class X3DHOrchestratorTests
{
    private Mock<IX3DHManager> _mockX3dhManager = null!;
    private X3DHOrchestrator _orchestrator = null!;
    private ActiveIdentityContext _activeIdentityContext = null!;

    // Local keys
    private X3dhKeys _localKeys = null!;

    // Remote keys
    private ECDsa _remoteIdentitySigningKey = null!;
    private ECDiffieHellman _remoteIdentityAgreementKey = null!;
    private ECDiffieHellman _remoteSignedPreKey = null!;
    private ECDiffieHellman _remoteOneTimePreKey = null!;


    [SetUp]
    public void Setup()
    {
        _mockX3dhManager = new Mock<IX3DHManager>();
        _activeIdentityContext = new ActiveIdentityContext();

        // Local keys setup
        var localIdentity = new IdentityRecord(Guid.NewGuid(), "Local Identity");
        var localIdentitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var localIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var localSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var localOneTimePreKeys = new[] { ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) };
        _localKeys = new X3dhKeys(localIdentitySigningKey, localIdentityAgreementKey, localSignedPreKey, localOneTimePreKeys);

        // Remote keys setup
        _remoteIdentitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _remoteIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _remoteSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _remoteOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Populate ActiveIdentityContext
        _activeIdentityContext.Identity = localIdentity;
        _activeIdentityContext.Keys = _localKeys;

        _orchestrator = new X3DHOrchestrator(
            _activeIdentityContext,
            _mockX3dhManager.Object
        );
    }

    [TearDown]
    public void TearDown()
    {
        _localKeys.Dispose();
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
        var remoteBundle = new ContractsPreKeyBundle
        {
            IdentityKey = ByteString.CopyFrom(_remoteIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(remoteSignedPreKeyBytes),
            PreKeySignature = ByteString.CopyFrom(_remoteIdentitySigningKey.SignData(remoteSignedPreKeyBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            OneTimePreKey = ByteString.CopyFrom(_remoteOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo())
        };

        var expectedSharedSecret = new SharedSecret(new byte[32]);
        Random.Shared.NextBytes(expectedSharedSecret.Value);
        var handshakeResult = new HandshakeInitiationResult(expectedSharedSecret, new PublicKey(new byte[65]));

        _mockX3dhManager.Setup(x => x.InitiateHandshake(
                It.IsAny<CryptoPreKeyBundle>(),
                _localKeys.IdentitySigningKey,
                _localKeys.IdentityAgreementKey))
            .Returns(handshakeResult);

        // Act
        var result = _orchestrator.InitiateHandshake(remotePeerId, remoteBundle);

        // Assert
        result.Should().NotBeNull();
        result.SharedSecret.Value.Should().BeEquivalentTo(expectedSharedSecret.Value);
        result.InitialRatchetPublicKey.Should().NotBeNull();
        _mockX3dhManager.Verify(x => x.InitiateHandshake(
            It.IsAny<CryptoPreKeyBundle>(),
            _localKeys.IdentitySigningKey,
            _localKeys.IdentityAgreementKey), Times.Once);
    }

    [Test]
    public void ProcessHandshake_WithValidBundle_ReturnsCorrectResult()
    {
        // Arrange
        var remotePeerId = new SessionPeerId(Guid.NewGuid());

        // The initiator's ephemeral key for this handshake
        using var remoteEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var remoteEphemeralPublicKey = remoteEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo();

        // The initiator's pre-key bundle
        var remoteSignedPreKeyBytes = _remoteSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var signature = _remoteIdentitySigningKey.SignData(remoteSignedPreKeyBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var initiatorPreKeyBundle = new ContractsPreKeyBundle
        {
            IdentityKey = ByteString.CopyFrom(_remoteIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(remoteSignedPreKeyBytes),
            PreKeySignature = ByteString.CopyFrom(signature)
        };

        var expectedSharedSecret = new SharedSecret(new byte[32]);
        Random.Shared.NextBytes(expectedSharedSecret.Value);

        _mockX3dhManager.Setup(x => x.RespondToHandshake(
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                _localKeys.IdentitySigningKey,
                _localKeys.IdentityAgreementKey,
                _localKeys.SignedPreKey,
                It.IsAny<ECDiffieHellman>()))
            .Returns(expectedSharedSecret);

        // Act
        var result = _orchestrator.ProcessHandshake(remotePeerId, initiatorPreKeyBundle, remoteEphemeralPublicKey);

        // Assert
        result.Should().NotBeNull();
        result.SharedSecret.Value.Should().BeEquivalentTo(expectedSharedSecret.Value);
        result.InitialRatchetPublicKey.Should().NotBeNull();
        _mockX3dhManager.Verify(x => x.RespondToHandshake(
            It.IsAny<byte[]>(),
            It.IsAny<byte[]>(),
            _localKeys.IdentitySigningKey,
            _localKeys.IdentityAgreementKey,
            _localKeys.SignedPreKey,
            It.IsAny<ECDiffieHellman>()), Times.Once);
    }
}
