using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Application.Chat;
using Percolator.Cryptography;

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
        var senderPkh = "test-pkh";
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
        var senderPkh = "test-pkh";
        var validTimestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(-30); // Within 60 seconds
        var signature = Signature.FromBytes(new byte[64]);

        _peerIdentityQueriesMock
            .Setup(x => x.GetPublicKeyByPkhAsync(senderPkh, It.IsAny<CancellationToken>()))
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
        var senderPkh = "test-pkh";
        var validTimestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(-30);
        
        // Generate a valid EC key pair
        using var ecdsa = ECDsa.Create();
        var publicKeyBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var publicKey = RatchetIdentityKey.FromBytes(publicKeyBytes);
        
        // Create an invalid signature (random bytes)
        var signature = Signature.FromBytes(new byte[64]);

        _peerIdentityQueriesMock
            .Setup(x => x.GetPublicKeyByPkhAsync(senderPkh, It.IsAny<CancellationToken>()))
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
        var senderPkh = Convert.ToBase64String(new byte[32]); // Valid base64 PKH
        var validTimestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(-30); // Must be within 60 seconds
        
        // Generate a valid EC key pair
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var publicKey = RatchetIdentityKey.FromBytes(publicKeyBytes);
        
        // Actually sign the correct payload
        var payload = System.Text.Encoding.UTF8.GetBytes($"{senderPkh}{validTimestamp.ToUnixTimeSeconds()}");
        var rawSig = ecdsa.SignData(payload, HashAlgorithmName.SHA256);
        var signature = Signature.FromBytesOwned(rawSig);

        _peerIdentityQueriesMock
            .Setup(x => x.GetPublicKeyByPkhAsync(senderPkh, It.IsAny<CancellationToken>()))
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
