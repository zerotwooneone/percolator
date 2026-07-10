using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Chat;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Chat;

[TestFixture]
public sealed class SealedSenderAuthenticationRoundTripTests
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
    public async Task SealedSenderAuthenticationRoundTrip_ClientSignedRequest_ValidatedByPeerAuthenticationService()
    {
        // Arrange
        var senderPublicIdentityId = new Percolator.Identity.PublicIdentityId(Guid.NewGuid());
        var validTimestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(-30); // Must be within 60 seconds
        
        // Generate a valid EC key pair to simulate the peer's public key
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var publicKey = RatchetIdentityKey.FromBytes(publicKeyBytes);
        
        // Sign the payload using the same format as the service: {senderPublicIdentityId}{timestamp}
        var payload = System.Text.Encoding.UTF8.GetBytes($"{senderPublicIdentityId}{validTimestamp.ToUnixTimeSeconds()}");
        var rawSig = ecdsa.SignData(payload, HashAlgorithmName.SHA256);
        var signature = Signature.FromBytesOwned(rawSig);

        _peerIdentityQueriesMock
            .Setup(x => x.GetPublicKeyByPublicIdentityIdAsync(senderPublicIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(publicKey);

        // Act
        var result = await _service.AuthenticateDeliveryCertificateRequestAsync(
            senderPublicIdentityId,
            validTimestamp,
            signature,
            CancellationToken.None);

        // Assert
        // We verify the actual boolean result, completely black-boxing the internals
        Assert.That(result, Is.True);
    }
}
