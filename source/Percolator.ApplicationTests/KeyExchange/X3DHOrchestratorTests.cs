using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.ApplicationTests.KeyExchange;

public class X3DHOrchestratorTests : IDisposable
{
    private Mock<IX3DHManager> _mockX3dhManager = null!;
    private X3DHOrchestrator _orchestrator = null!;
    private ActiveIdentityContext _activeIdentityContext = null!;

    // Local keys
    private X3dhKeys _localKeys = null!;

    // Remote keys
    private ECDiffieHellman _remoteIdentitySigningKey = null!;
    private ECDiffieHellman _remoteSignedPreKey = null!;
    private ECDiffieHellman _remoteOneTimePreKey = null!;


    [SetUp]
    public void Setup()
    {
        _mockX3dhManager = new Mock<IX3DHManager>();
        _activeIdentityContext = new ActiveIdentityContext();

        // Local keys setup
        var localIdentity = new IdentityRecord(Guid.NewGuid(), "Local Identity");
        var localIdentitySigningKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var localSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _localKeys = new X3dhKeys(localIdentitySigningKey, localSignedPreKey);
        
        // Set properties directly instead of using LoadKeys method
        _activeIdentityContext.Identity = localIdentity;
        _activeIdentityContext.Keys = _localKeys;

        // Remote keys setup
        _remoteIdentitySigningKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _remoteSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _remoteOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        _orchestrator = new X3DHOrchestrator(
            _activeIdentityContext,
            _mockX3dhManager.Object,
            NullLogger<X3DHOrchestrator>.Instance);
    }

    public void Dispose()
    {
        // Dispose individual keys in _localKeys
        _localKeys?.IdentitySigningKey.Dispose();
        _localKeys?.SignedPreKey.Dispose();
        _remoteIdentitySigningKey?.Dispose();
        _remoteSignedPreKey?.Dispose();
        _remoteOneTimePreKey?.Dispose();
    }

    [Test]
    public void CompleteHandshake_WithValidBundle_ReturnsCorrectResult()
    {
        // Arrange
        var remoteSignedPreKeyBytes = _remoteSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var remoteIdentitySigningKeyBytes = _remoteIdentitySigningKey.ExportSubjectPublicKeyInfo();
        var remoteBundle = new X3dPreKeyBundle
        (
            new RatchetIdentityKey(remoteIdentitySigningKeyBytes),
            new PreKey(remoteSignedPreKeyBytes),
            new OneTimeKey(_remoteOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo())
        );

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
                It.IsAny<X3dPreKeyBundle>(),
                It.IsAny<ECDiffieHellman>(),
                It.IsAny<ECDiffieHellman>()))
            .Returns(expectedSharedSecret);

        // Act
        var result = _orchestrator.InitiateHandshake(remoteBundle, ephemeralKey);

        // Assert
        result.Should().NotBeNull();
        result.Value.Should().BeEquivalentTo(expectedSharedSecret.Value);
        _mockX3dhManager.Verify(x => x.InitiateHandshake(
            It.Is<X3dPreKeyBundle>(b => b.IdentitySigningKey.Value.SequenceEqual(remoteIdentitySigningKeyBytes)),
            It.IsAny<ECDiffieHellman>(),
            It.IsAny<ECDiffieHellman>()), Times.Once);
    }

    [Test]
    public void CompleteHandshake_WithoutOneTimePreKey_ReturnsCorrectResult()
    {
        // Arrange
        var remoteSignedPreKeyBytes = _remoteSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var remoteIdentitySigningKeyBytes = _remoteIdentitySigningKey.ExportSubjectPublicKeyInfo();
        var remoteBundle = new X3dPreKeyBundle
        (
            new RatchetIdentityKey(remoteIdentitySigningKeyBytes),
            new PreKey(remoteSignedPreKeyBytes),
            null // OneTimePreKey intentionally omitted
        );

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
                It.IsAny<X3dPreKeyBundle>(),
                It.IsAny<ECDiffieHellman>(),
                It.IsAny<ECDiffieHellman>()))
            .Returns(expectedSharedSecret);

        // Act
        var result = _orchestrator.InitiateHandshake(remoteBundle, ephemeralKey);

        // Assert
        result.Should().NotBeNull();
        result.Value.Should().BeEquivalentTo(expectedSharedSecret.Value);
        
        // Verify that InitiateHandshake was called with a null one-time prekey
        _mockX3dhManager.Verify(x => x.InitiateHandshake(
            It.Is<X3dPreKeyBundle>(b => 
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
        var ratchetIdentityKey = new RatchetIdentityKey(remoteIdentitySigningKeyBytes);
        
        var expectedSharedSecret = new SharedSecret(new byte[32]);
        Random.Shared.NextBytes(expectedSharedSecret.Value);
        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ratchetEphemeralKey = new RatchetEphemeralKey(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo());

        _mockX3dhManager.Setup(x => x.VerifySignature(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<PreKey>(),
                It.IsAny<Signature>()))
            .Returns(true);

        _mockX3dhManager.Setup(x => x.RespondToHandshake(
                It.Is<RatchetIdentityKey>(k => k.Value.SequenceEqual(ratchetIdentityKey.Value)),
                It.Is<RatchetEphemeralKey>(k => k.Value.SequenceEqual(ratchetEphemeralKey.Value)),
                It.Is<RatchetIdentityKey>(k => k.Value.SequenceEqual(_localKeys.IdentitySigningKey.ExportECPrivateKey())),
                It.Is<PrivatePreKey>(k => k.Value.SequenceEqual(_localKeys.SignedPreKey.ExportECPrivateKey())),
                It.IsAny<PrivateOneTimeKey>()))
            .Returns(expectedSharedSecret);

        _mockX3dhManager.Setup(x => x.SignPreKey(
                _localKeys.IdentitySigningKey,
                It.IsAny<PreKey>()))
            .Returns(new Signature(new byte[64]));

        // Act
        var result = _orchestrator.CompleteHandshake(
            ratchetIdentityKey,
            ratchetEphemeralKey,
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

        // Assert
        result.Should().NotBeNull();
        result.SharedSecret.Value.Should().BeEquivalentTo(expectedSharedSecret.Value);
        result.ResponderBundle.Should().NotBeNull();
        result.ResponderBundle.IdentitySigningKey.Should().NotBeNull();
    }
    
    [Test]
    public void ProcessHandshake_WithOneTimePreKey_IncludesOneTimeKeyInResponse()
    {
        // Arrange
        var remoteIdentitySigningKeyBytes = _remoteIdentitySigningKey.ExportSubjectPublicKeyInfo();
        var ratchetIdentityKey = new RatchetIdentityKey(remoteIdentitySigningKeyBytes);
        
        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ratchetEphemeralKey = new RatchetEphemeralKey(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo());
        
        using var oneTimeKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var oneTimeKeyPublic = oneTimeKey.PublicKey.ExportSubjectPublicKeyInfo();

        _mockX3dhManager.Setup(x => x.VerifySignature(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<PreKey>(),
                It.IsAny<Signature>()))
            .Returns(true);

       _mockX3dhManager.Setup(x => x.RespondToHandshake(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<PrivatePreKey>(),
                It.Is<PrivateOneTimeKey>(k => k != null)))
            .Returns(new SharedSecret(new byte[32]));

        _mockX3dhManager.Setup(x => x.SignPreKey(
                _localKeys.IdentitySigningKey,
                It.IsAny<PreKey>()))
            .Returns(new Signature(new byte[64]));

        // Act
        var result = _orchestrator.CompleteHandshake(
            ratchetIdentityKey,
            ratchetEphemeralKey,
            oneTimeKey);

        // Assert
        result.Should().NotBeNull();
        result.ResponderBundle.Should().NotBeNull();
        result.ResponderBundle.OneTimePreKey.Should().NotBeNull();
        result.ResponderBundle.OneTimePreKey.Value.Should().BeEquivalentTo(oneTimeKeyPublic);
    }
}
