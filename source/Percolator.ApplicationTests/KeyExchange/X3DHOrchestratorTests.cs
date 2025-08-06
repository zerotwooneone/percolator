using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using CryptoPreKeyBundle = Percolator.Cryptography.PreKeyBundle;

namespace Percolator.ApplicationTests.KeyExchange;

public class X3DHOrchestratorTests : IDisposable
{
    private Mock<IX3DHManager> _mockX3dhManager = null!;
    private Mock<IOneTimeKeyProvider> _mockOneTimeKeyProvider = null!;
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
        _mockOneTimeKeyProvider = new Mock<IOneTimeKeyProvider>();
        _activeIdentityContext = new ActiveIdentityContext();

        // Local keys setup
        var localIdentity = new IdentityRecord(Guid.NewGuid(), "Local Identity");
        var localIdentitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var localIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var localSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _localKeys = new X3dhKeys(localIdentitySigningKey, localIdentityAgreementKey, localSignedPreKey);
        
        // Set properties directly instead of using LoadKeys method
        _activeIdentityContext.Identity = localIdentity;
        _activeIdentityContext.Keys = _localKeys;

        // Remote keys setup
        _remoteIdentitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _remoteIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _remoteSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _remoteOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        _orchestrator = new X3DHOrchestrator(
            _activeIdentityContext,
            _mockX3dhManager.Object,
            _mockOneTimeKeyProvider.Object,
            NullLogger<X3DHOrchestrator>.Instance);
    }

    public void Dispose()
    {
        // Dispose individual keys in _localKeys
        _localKeys?.IdentitySigningKey.Dispose();
        _localKeys?.IdentityAgreementKey.Dispose();
        _localKeys?.SignedPreKey.Dispose();
        _remoteIdentitySigningKey?.Dispose();
        _remoteIdentityAgreementKey?.Dispose();
        _remoteSignedPreKey?.Dispose();
        _remoteOneTimePreKey?.Dispose();
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
        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        _mockX3dhManager.Setup(x => x.VerifySignature(
            It.Is<RatchetIdentityKey>(k => k.Value.SequenceEqual(remoteIdentitySigningKeyBytes)),
            It.Is<PreKey>(k => k.Value.SequenceEqual(remoteSignedPreKeyBytes)),
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
    public void CompleteHandshake_WithoutOneTimePreKey_ReturnsCorrectResult()
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
            // OneTimePreKey intentionally omitted
        };

        var expectedSharedSecret = new SharedSecret(new byte[32]);
        Random.Shared.NextBytes(expectedSharedSecret.Value);
        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        _mockX3dhManager.Setup(x => x.VerifySignature(
            It.Is<RatchetIdentityKey>(k => k.Value.SequenceEqual(remoteIdentitySigningKeyBytes)),
            It.Is<PreKey>(k => k.Value.SequenceEqual(remoteSignedPreKeyBytes)),
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
        
        // Verify that InitiateHandshake was called with a null one-time prekey
        _mockX3dhManager.Verify(x => x.InitiateHandshake(
            It.Is<CryptoPreKeyBundle>(b => 
                b.IdentitySigningKey.Value.SequenceEqual(remoteIdentitySigningKeyBytes) && 
                b.OneTimePreKey == null),
            It.IsAny<ECDiffieHellman>(),
            It.IsAny<ECDiffieHellman>()), Times.Once);
    }

    [Test]
    public void ProcessHandshake_WithValidBundle_ReturnsCorrectResult()
    {
        // Arrange
        var remoteIdentitySigningKeyBytes = _remoteIdentitySigningKey.ExportSubjectPublicKeyInfo();
        var remoteSignedPreKeyBytes = _remoteSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var remoteBundle = new ContractsPreKeyBundle
        {
            IdentityAgreementKey = ByteString.CopyFrom(_remoteIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            IdentitySigningKey = ByteString.CopyFrom(remoteIdentitySigningKeyBytes),
            SignedPreKey = ByteString.CopyFrom(remoteSignedPreKeyBytes),
            PreKeySignature = ByteString.CopyFrom(_remoteIdentitySigningKey.SignData(remoteSignedPreKeyBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        };

        var expectedSharedSecret = new SharedSecret(new byte[32]);
        Random.Shared.NextBytes(expectedSharedSecret.Value);
        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralKeyBytes = ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo();

        _mockX3dhManager.Setup(x => x.VerifySignature(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<PreKey>(),
                It.IsAny<Signature>()))
            .Returns(true);

        _mockX3dhManager.Setup(x => x.RespondToHandshake(
                It.Is<RatchetIdentityKey>(k => k.Value.SequenceEqual(_remoteIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo())),
                It.Is<RatchetEphemeralKey>(k => k.Value.SequenceEqual(ephemeralKeyBytes)),
                It.Is<PrivateAgreementKey>(k => k.Value.SequenceEqual(_localKeys.IdentityAgreementKey.ExportECPrivateKey())),
                It.Is<PrivatePreKey>(k => k.Value.SequenceEqual(_localKeys.SignedPreKey.ExportECPrivateKey())),
                It.IsAny<PrivateOneTimeKey>()))
            .Returns(expectedSharedSecret);

        _mockX3dhManager.Setup(x => x.SignPreKey(
                _localKeys.IdentitySigningKey,
                It.IsAny<PreKey>()))
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
    
    [Test]
    public void ProcessHandshake_WithOneTimePreKey_IncludesOneTimeKeyInResponse()
    {
        // Arrange
        var remoteIdentitySigningKeyBytes = _remoteIdentitySigningKey.ExportSubjectPublicKeyInfo();
        var remoteBundle = new ContractsPreKeyBundle
        {
            IdentityAgreementKey = ByteString.CopyFrom(_remoteIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            IdentitySigningKey = ByteString.CopyFrom(remoteIdentitySigningKeyBytes),
            SignedPreKey = ByteString.CopyFrom(_remoteSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo()),
            PreKeySignature = ByteString.CopyFrom(_remoteIdentitySigningKey.SignData(
                _remoteSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo(), 
                HashAlgorithmName.SHA256, 
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        };

        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralKeyBytes = ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo();
        
        using var oneTimeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var oneTimeKeyBytes = oneTimeKey.ExportECPrivateKey();
        var oneTimeKeyPublic = oneTimeKey.PublicKey.ExportSubjectPublicKeyInfo();

        _mockX3dhManager.Setup(x => x.VerifySignature(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<PreKey>(),
                It.IsAny<Signature>()))
            .Returns(true);

        // Use PopOneTimeKey instead of GetOneTimeKeyAsync
        _mockOneTimeKeyProvider.Setup(x => x.PopOneTimeKey())
            .Returns(oneTimeKey);

        _mockX3dhManager.Setup(x => x.RespondToHandshake(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<PrivateAgreementKey>(),
                It.IsAny<PrivatePreKey>(),
                It.Is<PrivateOneTimeKey>(k => k != null)))
            .Returns(new SharedSecret(new byte[32]));

        _mockX3dhManager.Setup(x => x.SignPreKey(
                _localKeys.IdentitySigningKey,
                It.IsAny<PreKey>()))
            .Returns(new Signature(new byte[64]));

        // Act
        var result = _orchestrator.ProcessHandshake(remoteBundle, ephemeralKeyBytes);

        // Assert
        result.Should().NotBeNull();
        result.ResponderBundle.Should().NotBeNull();
        result.ResponderBundle.OneTimePreKey.Should().NotBeNull();
        result.ResponderBundle.OneTimePreKey.ToByteArray().Should().BeEquivalentTo(oneTimeKeyPublic);
    }
}
