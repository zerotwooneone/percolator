using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class CryptoBoundaryTests
{
    [Test]
    public void EncryptDecryptAesGcm_WithEmptyMessage_ShouldSucceed()
    {
        // Arrange
        byte[] key = RandomNumberGenerator.GetBytes(32); // AES-256
        byte[] emptyPlaintext = Array.Empty<byte>();
        byte[] associatedData = "metadata"u8.ToArray();

        // Act
        var ciphertext = CryptoUtils.EncryptAesGcm(key, 1, emptyPlaintext, associatedData);
        var decrypted = CryptoUtils.DecryptAesGcm(key, 1, ciphertext, associatedData);

        // Assert
        decrypted.Should().BeEmpty();
    }

    [Test]
    public void SignVerify_WithEmptyMessage_ShouldSucceed()
    {
        // Arrange
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifyingKey = ECDsa.Create(signingKey.ExportParameters(false));
        byte[] emptyMessage = Array.Empty<byte>();

        // Act
        var signature = CryptoUtils.Sign(emptyMessage, signingKey);
        var isValid = CryptoUtils.Verify(emptyMessage, signature, verifyingKey);

        // Assert
        signature.Should().NotBeEmpty();
        isValid.Should().BeTrue();
    }

    [Test]
    public void DoubleRatchetSession_WithEmptyMessage_ShouldRoundtripSuccessfully()
    {
        // Arrange
        // Create two sessions for Alice and Bob
        using var aliceIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Derive a shared secret (in a real scenario, this would come from X3DH)
        var sharedSecret = new SharedSecret(aliceIdentity.DeriveKeyMaterial(bobIdentity.PublicKey));

        // Create sessions
        var aliceSession = DoubleRatchetSession.AsInitiator(
            sharedSecret,
            new RatchetIdentityKey(bobIdentity.PublicKey.ExportSubjectPublicKeyInfo()),
            new RatchetEphemeralKey(bobEphemeral.PublicKey.ExportSubjectPublicKeyInfo()));

        var bobSession = DoubleRatchetSession.AsResponder(
            sharedSecret,
            new RatchetIdentityKey(aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo()),
            bobEphemeral);

        // Act - Encrypt and decrypt an empty message
        var emptyPlaintext = new Plaintext(Array.Empty<byte>());
        var message = aliceSession.Encrypt(emptyPlaintext);
        var decrypted = bobSession.Decrypt(message);

        // Assert
        decrypted.Value.Should().BeEmpty();
    }

    [Test]
    public void SenderKeySession_WithEmptyMessage_ShouldRoundtripSuccessfully()
    {
        // Arrange
        byte[] sessionKey = RandomNumberGenerator.GetBytes(32);
        byte[] context = "group-context"u8.ToArray();
        
        var senderSession = new SenderKeySession(sessionKey, context);
        var receiverSession = new SenderKeySession(sessionKey, context);
        
        // Act
        var emptyMessage = Array.Empty<byte>();
        var encrypted = senderSession.Encrypt(emptyMessage);
        var decrypted = receiverSession.Decrypt(encrypted);
        
        // Assert
        decrypted.Should().BeEmpty();
        
        // Clean up
        senderSession.Dispose();
        receiverSession.Dispose();
    }

    [Test]
    public void EncryptDecryptAesGcm_WithMaxKeySizes_ShouldSucceed()
    {
        // Arrange - Use maximum recommended key size (32 bytes for AES-256)
        byte[] key = RandomNumberGenerator.GetBytes(32);
        // Create a larger plaintext (1 MB)
        byte[] largePlaintext = RandomNumberGenerator.GetBytes(1024 * 1024);
        byte[] associatedData = "metadata"u8.ToArray();

        // Act
        var ciphertext = CryptoUtils.EncryptAesGcm(key, 1, largePlaintext, associatedData);
        var decrypted = CryptoUtils.DecryptAesGcm(key, 1, ciphertext, associatedData);

        // Assert
        decrypted.Should().BeEquivalentTo(largePlaintext);
    }

    [Test]
    public void SessionRatchetMessage_WithEmptyMessage_ShouldSerializeDeserializeSuccessfully()
    {
        // Arrange
        using var keyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ratchetKey = new RatchetEphemeralKey(keyPair.PublicKey.ExportSubjectPublicKeyInfo());
        ulong counter = 1;
        var emptyCiphertext = new Ciphertext(Array.Empty<byte>());

        // Act
        var message = SessionRatchetMessage.Create(ratchetKey, counter, emptyCiphertext);
        var serialized = message.Value;
        var deserialized = new SessionRatchetMessage(serialized);

        // Assert
        deserialized.GetCiphertext().Value.Should().BeEmpty();
        var (key, count) = deserialized.GetHeader();
        key.Value.Should().BeEquivalentTo(ratchetKey.Value);
        count.Should().Be(counter);
    }

    [Test] 
    public void CryptoUtils_WithInvalidKeySize_ThrowsException()
    {
        // Arrange - Use an invalid key size that's too short for AES (e.g., 8 bytes)
        byte[] invalidKey = RandomNumberGenerator.GetBytes(8); // Too short
        byte[] plaintext = "message"u8.ToArray();
        byte[] associatedData = "metadata"u8.ToArray();

        // Act & Assert
        Assert.Throws<CryptographicException>(() => 
            CryptoUtils.EncryptAesGcm(invalidKey, 1, plaintext, associatedData));
    }
}
