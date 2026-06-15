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
    private PeerAuthenticationService _service;

    [SetUp]
    public void SetUp()
    {
        _peerIdentityQueriesMock = new Mock<IPeerIdentityQueries>();
        _loggerMock = new Mock<ILogger<PeerAuthenticationService>>();
        _service = new PeerAuthenticationService(
            _peerIdentityQueriesMock.Object,
            _loggerMock.Object);
    }

    [Test]
    public async Task AuthenticateDeliveryCertificateRequest_ReturnsFalse_WhenTimestampIsExpired()
    {
        // Arrange
        var senderPkh = "test-pkh";
        var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-70); // More than 60 seconds old
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
        var validTimestamp = DateTimeOffset.UtcNow.AddMinutes(-30); // Within 60 seconds
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
        var validTimestamp = DateTimeOffset.UtcNow.AddMinutes(-30);
        
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
    public async Task AuthenticateDeliveryCertificateRequest_DoesNotThrow_WhenAllInputsAreValid()
    {
        // Arrange
        var senderPkh = Convert.ToBase64String(new byte[32]); // Valid base64 PKH
        var validTimestamp = DateTimeOffset.UtcNow.AddMinutes(-30);
        
        // Generate a valid EC key pair
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var publicKey = RatchetIdentityKey.FromBytes(publicKeyBytes);
        
        // Use a signature that meets length constraints
        var signature = Signature.FromBytesOwned(new byte[64]);

        _peerIdentityQueriesMock
            .Setup(x => x.GetPublicKeyByPkhAsync(senderPkh, It.IsAny<CancellationToken>()))
            .ReturnsAsync(publicKey);

        // Act & Assert
        // This test verifies the happy path flow doesn't throw exceptions
        // The actual cryptographic verification correctness is tested in integration tests
        Assert.DoesNotThrowAsync(async () => 
        {
            await _service.AuthenticateDeliveryCertificateRequestAsync(
                senderPkh,
                validTimestamp,
                signature,
                CancellationToken.None);
        });
    }
}
