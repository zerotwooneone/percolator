using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Chat;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Chat;

[TestFixture]
public sealed class PeerAuthenticationServiceTests
{
    private Mock<IPeerIdentityQueries> _peerIdentityQueriesMock;
    private Mock<ILogger<PeerAuthenticationService>> _loggerMock;
    private Mock<TimeProvider> _timeProviderMock;
    private PeerAuthenticationService _service;

    [SetUp]
    public void SetUp()
    {
        _peerIdentityQueriesMock = new Mock<IPeerIdentityQueries>();
        _loggerMock = new Mock<ILogger<PeerAuthenticationService>>();
        _timeProviderMock = new Mock<TimeProvider>();
        
        // Setup deterministic time
        _timeProviderMock.Setup(x => x.GetUtcNow()).Returns(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));

        _service = new PeerAuthenticationService(
            _peerIdentityQueriesMock.Object,
            _loggerMock.Object,
            _timeProviderMock.Object);
    }

    [Test]
    public async Task AuthenticateDeliveryCertificateRequest_ReturnsFalse_WhenTimestampIsExpired()
    {
        // Arrange
        var senderPkhBytes = new byte[32];
        var senderPkh = Percolator.Chat.Messaging.ValueObjects.Pkh.FromBytes(senderPkhBytes);
        var expiredTimestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(-70); // More than 60 seconds old
        var signature = Signature.FromBytes(new byte[64]);

        // Act
        var result = await _service.AuthenticateDeliveryCertificateRequestAsync(
            senderPkh,
            expiredTimestamp,
            signature,
            CancellationToken.None);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task AuthenticateDeliveryCertificateRequest_ReturnsFalse_WhenPeerNotFound()
    {
        // Arrange
        var senderPkhBytes = new byte[32];
        var senderPkh = Percolator.Chat.Messaging.ValueObjects.Pkh.FromBytes(senderPkhBytes);
        var identityPublicKeyHash = IdentityPublicKeyHash.FromBytes(senderPkhBytes);
        var validTimestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(-30); // Within 60 seconds
        var signature = Signature.FromBytes(new byte[64]);

        _peerIdentityQueriesMock
            .Setup(x => x.GetPublicKeyByPkhAsync(identityPublicKeyHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RatchetIdentityKey?)null);

        // Act
        var result = await _service.AuthenticateDeliveryCertificateRequestAsync(
            senderPkh,
            validTimestamp,
            signature,
            CancellationToken.None);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task AuthenticateDeliveryCertificateRequest_ReturnsFalse_WhenSignatureVerificationFails()
    {
        // Arrange
        var senderPkhBytes = new byte[32];
        var senderPkh = Percolator.Chat.Messaging.ValueObjects.Pkh.FromBytes(senderPkhBytes);
        var identityPublicKeyHash = IdentityPublicKeyHash.FromBytes(senderPkhBytes);
        var validTimestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(-30);
        
        // Generate a valid EC key pair
        using var ecdsa = ECDsa.Create();
        var publicKeyBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var publicKey = RatchetIdentityKey.FromBytes(publicKeyBytes);
        
        // Create an invalid signature (random bytes)
        var signature = Signature.FromBytes(new byte[64]);

        _peerIdentityQueriesMock
            .Setup(x => x.GetPublicKeyByPkhAsync(identityPublicKeyHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(publicKey);

        // Act
        var result = await _service.AuthenticateDeliveryCertificateRequestAsync(
            senderPkh,
            validTimestamp,
            signature,
            CancellationToken.None);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task AuthenticateDeliveryCertificateRequest_ReturnsTrue_WhenAllInputsAreValid()
    {
        // Arrange
        var senderPkhBytes = new byte[32];
        var senderPkh = Percolator.Chat.Messaging.ValueObjects.Pkh.FromBytes(senderPkhBytes);
        var identityPublicKeyHash = IdentityPublicKeyHash.FromBytes(senderPkhBytes);
        var validTimestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(-30); // Must be within 60 seconds
        
        // Generate a valid EC key pair
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var publicKey = RatchetIdentityKey.FromBytes(publicKeyBytes);
        
        // Sign the payload using the same format as the service: {senderPkh}{timestamp}
        var payload = System.Text.Encoding.UTF8.GetBytes($"{senderPkh}{validTimestamp.ToUnixTimeSeconds()}");
        var rawSig = ecdsa.SignData(payload, HashAlgorithmName.SHA256);
        var signature = Signature.FromBytesOwned(rawSig);

        _peerIdentityQueriesMock
            .Setup(x => x.GetPublicKeyByPkhAsync(identityPublicKeyHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(publicKey);

        // Act
        var result = await _service.AuthenticateDeliveryCertificateRequestAsync(
            senderPkh,
            validTimestamp,
            signature,
            CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
    }
}
