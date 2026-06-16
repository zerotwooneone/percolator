using NUnit.Framework;
using Percolator.Application.Chat;

namespace Percolator.ApplicationTests.Chat;

[TestFixture]
public sealed class DeliveryCertificateWireFormatterTests
{
    [Test]
    public void DeliveryCertificateWireFormatter_RoundTrip_MaintainsDataIntegrity()
    {
        // Arrange
        var originalFingerprint = new byte[32];
        Random.Shared.NextBytes(originalFingerprint); // Generate random 32-byte hash
        
        // Truncate to seconds because UnixTimeSeconds drops milliseconds
        var originalExpiration = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds()); 

        // Act - Pack
        var payload = DeliveryCertificateWireFormatter.Pack(originalFingerprint, originalExpiration);

        // Assert - Bounds
        Assert.That(payload.Length, Is.EqualTo(40));

        // Act - Unpack
        var extractedExpiration = DeliveryCertificateWireFormatter.ExtractExpiration(payload);
        var extractedFingerprint = payload.AsSpan(0, 32).ToArray();

        // Assert - Integrity
        Assert.That(extractedExpiration, Is.EqualTo(originalExpiration));
        Assert.That(originalFingerprint.SequenceEqual(extractedFingerprint), Is.True);
    }

    [Test]
    public void Pack_Throws_WhenFingerprintLengthIsInvalid()
    {
        // Arrange
        var invalidFingerprint = new byte[31]; // Wrong length
        var expiration = DateTimeOffset.UtcNow;

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            DeliveryCertificateWireFormatter.Pack(invalidFingerprint, expiration));
    }

    [Test]
    public void ExtractExpiration_Throws_WhenPayloadLengthIsInvalid()
    {
        // Arrange
        var invalidPayload = new byte[39]; // Wrong length

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            DeliveryCertificateWireFormatter.ExtractExpiration(invalidPayload));
    }
}
