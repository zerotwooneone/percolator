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
        for(int i = 0; i < 32; i++) originalFingerprint[i] = (byte)i; // Deterministic arbitrary byte array
        
        var originalExpiration = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

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
        var expiration = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

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
