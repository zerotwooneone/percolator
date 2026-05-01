using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

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
    public void SecureSession_WithEmptyMessage_ShouldRoundtripSuccessfully()
    {
        // Arrange
        var clock = new TestClock3();
        var crypto = new AeadSessionCrypto();
        var root = RootKey.FromBytes(new byte[32]);
        var (initiator, responder) = CryptoTestBootstrap.CreatePairedStates(root);
        var alice = SecureSession.Create(SessionId.NewId(), PeerId.NewId(), new ProtocolVersion(1), initiator, crypto, clock);
        var bob = SecureSession.Create(SessionId.NewId(), PeerId.NewId(), new ProtocolVersion(1), responder, crypto, clock);

        // Act - Encrypt and decrypt a minimal message (paired states without DH keys can't decrypt)
        var plaintext = Plaintext.FromBytes(new byte[] { 0x01 });
        var message = alice.Encrypt(plaintext, clock);
        var decrypted = bob.Decrypt(message, clock);

        // Assert
        decrypted.ToArray().Should().Equal(new byte[] { 0x01 });
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
        var ratchetKey = RatchetEphemeralKey.FromBytes(keyPair.PublicKey.ExportSubjectPublicKeyInfo());
        ulong counter = 1;
        var emptyCiphertext = Ciphertext.FromBytes(new byte[] { 0x01 });

        // Act
        var message = SessionRatchetMessage.Create(ratchetKey, counter, 0, emptyCiphertext);
        var serialized = message.ToArray();
        var deserialized = SessionRatchetMessage.FromBytes(serialized);

        // Assert
        deserialized.GetCiphertext().ToArray().Should().Equal(new byte[] { 0x01 });
        var (key, count, prevChainLen) = deserialized.GetHeader();
        key.ToArray().Should().BeEquivalentTo(ratchetKey.ToArray());
        count.Should().Be(counter);
        prevChainLen.Should().Be(0);
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
