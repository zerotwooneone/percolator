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
        var ratchetKey = new RatchetEphemeralKey(keyPair.PublicKey.ExportSubjectPublicKeyInfo());
        ulong counter = 42;
        byte[] data = "Encrypted data"u8.ToArray();
        var ciphertext = new Ciphertext(data);

        // Act
        var message = SessionRatchetMessage.Create(ratchetKey, counter, ciphertext);

        // Assert
        message.Should().NotBeNull();
        var (retrievedKey, retrievedCounter) = message.GetHeader();
        retrievedKey.Value.Should().BeEquivalentTo(ratchetKey.Value);
        retrievedCounter.Should().Be(counter);
        message.GetCiphertext().Value.Should().BeEquivalentTo(ciphertext.Value);
    }

    [Test]
    public void ValueRoundTrip_ShouldPreserveData()
    {
        // Arrange
        using var keyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ratchetKey = new RatchetEphemeralKey(keyPair.PublicKey.ExportSubjectPublicKeyInfo());
        ulong counter = 123;
        var ciphertext = new Ciphertext(RandomNumberGenerator.GetBytes(64));
        var original = SessionRatchetMessage.Create(ratchetKey, counter, ciphertext);

        // Act - Serialize by getting Value and deserialize by creating new instance
        var serialized = original.Value;
        var deserialized = new SessionRatchetMessage(serialized);

        // Assert
        serialized.Should().NotBeEmpty();
        deserialized.Should().NotBeNull();
        
        // Check that all properties roundtrip correctly
        var (originalKey, originalCounter) = original.GetHeader();
        var (deserializedKey, deserializedCounter) = deserialized.GetHeader();
        deserializedKey.Value.Should().BeEquivalentTo(originalKey.Value);
        deserializedCounter.Should().Be(originalCounter);
        deserialized.GetCiphertext().Value.Should().BeEquivalentTo(original.GetCiphertext().Value);
        
        // Header associated data should be deterministic
        deserialized.GetHeaderAssociatedData().Should().BeEquivalentTo(original.GetHeaderAssociatedData());
    }

    [Test]
    public void Constructor_WithInvalidData_ThrowsException()
    {
        // Arrange
        byte[] invalidData = RandomNumberGenerator.GetBytes(10); // Too short to be valid

        // Act & Assert - This should throw when trying to parse the protobuf data
        var message = new SessionRatchetMessage(invalidData);
        Assert.Throws<Google.Protobuf.InvalidProtocolBufferException>(() => message.GetHeader());
    }

    [Test]
    public void GetHeaderAssociatedData_ShouldReturnConsistentValue()
    {
        // Arrange
        using var keyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ratchetKey = new RatchetEphemeralKey(keyPair.PublicKey.ExportSubjectPublicKeyInfo());
        var message = SessionRatchetMessage.Create(
            ratchetKey, 
            1, 
            new Ciphertext("data"u8.ToArray()));
        
        // Act
        var associatedData1 = message.GetHeaderAssociatedData();
        var associatedData2 = message.GetHeaderAssociatedData();
        
        // Assert
        associatedData1.Should().NotBeEmpty();
        associatedData1.Should().BeEquivalentTo(associatedData2); // Should be deterministic
    }
}
