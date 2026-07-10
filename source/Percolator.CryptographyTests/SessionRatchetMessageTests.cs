using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class SessionRatchetMessageTests
{
    [Test]
    public void Create_WithValidParameters_ReturnsCorrectMessage()
    {
        // Arrange
        using var keyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ratchetKey = RatchetEphemeralKey.FromBytes(keyPair.PublicKey.ExportSubjectPublicKeyInfo());
        ulong counter = 42;
        ulong previousChainLength = 10;
        byte[] data = "Encrypted data"u8.ToArray();
        var ciphertext = Ciphertext.FromBytes(data);

        // Act
        var message = SessionRatchetMessage.Create(ratchetKey, counter, previousChainLength, ciphertext);

        // Assert
        message.Should().NotBeNull();
        var (retrievedKey, retrievedCounter, retrievedPreviousChainLength) = message.GetHeader();
        retrievedKey.ToArray().Should().BeEquivalentTo(ratchetKey.ToArray());
        retrievedCounter.Should().Be(counter);
        retrievedPreviousChainLength.Should().Be(previousChainLength);
        message.GetCiphertext().ToArray().Should().BeEquivalentTo(ciphertext.ToArray());
    }

    [Test]
    public void ValueRoundTrip_ShouldPreserveData()
    {
        // Arrange
        using var keyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ratchetKey = RatchetEphemeralKey.FromBytes(keyPair.PublicKey.ExportSubjectPublicKeyInfo());
        ulong counter = 123;
        ulong previousChainLength = 50;
        var ciphertext = Ciphertext.FromBytesOwned(RandomNumberGenerator.GetBytes(64));
        var original = SessionRatchetMessage.Create(ratchetKey, counter, previousChainLength, ciphertext);

        // Act - Serialize by getting ToArray and deserialize by creating new instance
        var serialized = original.ToArray();
        var deserialized = SessionRatchetMessage.FromBytes(serialized);

        // Assert
        serialized.Should().NotBeEmpty();
        deserialized.Should().NotBeNull();
        
        // Check that all properties roundtrip correctly
        var (originalKey, originalCounter, originalPreviousChainLength) = original.GetHeader();
        var (deserializedKey, deserializedCounter, deserializedPreviousChainLength) = deserialized.GetHeader();
        deserializedKey.ToArray().Should().BeEquivalentTo(originalKey.ToArray());
        deserializedCounter.Should().Be(originalCounter);
        deserializedPreviousChainLength.Should().Be(originalPreviousChainLength);
        deserialized.GetCiphertext().ToArray().Should().BeEquivalentTo(original.GetCiphertext().ToArray());
        
        // Header associated data should be deterministic
        deserialized.GetHeaderAssociatedData().Should().BeEquivalentTo(original.GetHeaderAssociatedData());
    }

    [Test]
    public void Constructor_WithInvalidData_ThrowsException()
    {
        // Arrange
        byte[] invalidData = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A }; // Fixed invalid data for determinism

        // Act & Assert - Invalid data should throw when getting header (exception type is implementation detail)
        var message = SessionRatchetMessage.FromBytes(invalidData);
        Assert.That(() => message.GetHeader(), Throws.Exception);
    }

    [Test]
    public void GetHeaderAssociatedData_ShouldReturnConsistentValue()
    {
        // Arrange
        using var keyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ratchetKey = RatchetEphemeralKey.FromBytes(keyPair.PublicKey.ExportSubjectPublicKeyInfo());
        var message = SessionRatchetMessage.Create(
            ratchetKey, 
            1,
            0, // previous chain length
            Ciphertext.FromBytes("data"u8.ToArray()));
        
        // Act
        var associatedData1 = message.GetHeaderAssociatedData();
        var associatedData2 = message.GetHeaderAssociatedData();
        
        // Assert
        associatedData1.Should().NotBeEmpty();
        associatedData1.Should().BeEquivalentTo(associatedData2); // Should be deterministic
    }
}
