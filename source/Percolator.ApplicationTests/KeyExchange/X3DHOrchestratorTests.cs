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
    public void CompleteHandshake_WithValidBundle_ReturnsCorrectResult()
    {
        // Arrange
        var remoteSignedPreKeyBytes = _remoteSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var remoteIdentitySigningKeyBytes = _remoteIdentitySigningKey.ExportSubjectPublicKeyInfo();
        var remoteBundle = new ContractsPreKeyBundle
        {
            IdentityAgreementKey = ByteString.CopyFrom(_remoteIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            IdentitySigningKey = ByteString.CopyFrom(remoteIdentitySigningKeyBytes),
            SignedPreKey = ByteString.CopyFrom(remoteSignedPreKeyBytes),
            PreKeySignature = ByteString.CopyFrom(_remoteIdentitySigningKey.SignData(remoteSignedPreKeyBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            OneTimePreKey = ByteString.CopyFrom(_remoteOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo())
        };

        var expectedSharedSecret = new SharedSecret(new byte[32]);
        Random.Shared.NextBytes(expectedSharedSecret.Value);
        using var ephemeralKey = ECDiffieHellman.Create();

        _mockX3dhManager.Setup(x => x.VerifySignature(
            It.Is<PublicKey>(k => k.Value.SequenceEqual(remoteIdentitySigningKeyBytes)),
            It.Is<PublicKey>(k => k.Value.SequenceEqual(remoteSignedPreKeyBytes)),
            It.IsAny<Signature>()))
            .Returns(true);

        _mockX3dhManager
            .Setup(x => x.InitiateHandshake(
                It.IsAny<CryptoPreKeyBundle>(),
                It.IsAny<ECDiffieHellman>(),
                It.IsAny<ECDiffieHellman>()))
            .Returns(expectedSharedSecret);

        // Act
        var result = _orchestrator.CompleteHandshake(remoteBundle, ephemeralKey);

        // Assert
        result.Should().NotBeNull();
        result.Value.Should().BeEquivalentTo(expectedSharedSecret.Value);
        _mockX3dhManager.Verify(x => x.InitiateHandshake(
            It.Is<CryptoPreKeyBundle>(b => b.IdentitySigningKey.Value.SequenceEqual(remoteIdentitySigningKeyBytes)),
            It.IsAny<ECDiffieHellman>(),
            It.IsAny<ECDiffieHellman>()), Times.Once);
    }

    [Test]
    public void ProcessHandshake_WithValidBundle_ReturnsCorrectResult()
    {
        // Arrange
        var remoteIdentitySigningKeyBytes = _remoteIdentitySigningKey.ExportSubjectPublicKeyInfo();
        var remoteBundle = new ContractsPreKeyBundle
        {
            IdentityAgreementKey = ByteString.CopyFrom(_remoteIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            IdentitySigningKey = ByteString.CopyFrom(remoteIdentitySigningKeyBytes),
            SignedPreKey = ByteString.CopyFrom(_remoteSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo()),
            PreKeySignature = ByteString.CopyFrom(new byte[64]) // Dummy signature
        };

        var expectedSharedSecret = new SharedSecret(new byte[32]);
        Random.Shared.NextBytes(expectedSharedSecret.Value);
        using var ephemeralKey = ECDiffieHellman.Create();
        var ephemeralKeyBytes = ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo();

        _mockX3dhManager.Setup(x => x.RespondToHandshake(
                It.Is<PublicKey>(k => k.Value.SequenceEqual(_remoteIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo())),
                It.Is<PublicKey>(k => k.Value.SequenceEqual(ephemeralKeyBytes)),
                _localKeys.IdentitySigningKey,
                _localKeys.IdentityAgreementKey,
                _localKeys.SignedPreKey,
                _localKeys.OneTimePreKeys.First()))
            .Returns(expectedSharedSecret);

        _mockX3dhManager.Setup(x => x.SignPreKey(
                _localKeys.IdentitySigningKey,
                It.IsAny<PublicKey>()))
            .Returns(new Signature(new byte[64]));

        // Act
        var result = _orchestrator.ProcessHandshake(remoteBundle, ephemeralKeyBytes);

        // Assert
        result.Should().NotBeNull();
        result.SharedSecret.Value.Should().BeEquivalentTo(expectedSharedSecret.Value);
        result.ResponderBundle.Should().NotBeNull();
        result.ResponderBundle.IdentityAgreementKey.Should().NotBeNull();
        result.ResponderBundle.IdentitySigningKey.Should().NotBeNull();
    }
}
