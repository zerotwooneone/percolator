using NUnit.Framework;
using Pecolator.Cryptography;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class SenderKeySessionTests
    {
        private byte[] _sessionKey;

        [SetUp]
        public void Setup()
        {
            _sessionKey = new byte[32];
            RandomNumberGenerator.Fill(_sessionKey);
        }

        [Test]
        public void Encrypt_WithNewSession_CreatesValidCiphertext()
        {
            // Arrange
            using var session = new SenderKeySession(_sessionKey);
            var originalPlaintext = "This is a test message.";
            var plaintextBytes = Encoding.UTF8.GetBytes(originalPlaintext);

            // Act
            var senderKeyMessage = session.Encrypt(plaintextBytes);

            // Assert
            senderKeyMessage.Should().NotBeNull();
            senderKeyMessage.Ciphertext.Should().NotBeNull();
            senderKeyMessage.Ciphertext.Should().NotBeEquivalentTo(plaintextBytes);
        }

        [Test]
        public void Decrypt_WithValidMessage_ReturnsOriginalPlaintext()
        {
            // Arrange
            using var senderSession = new SenderKeySession(_sessionKey);
            var originalPlaintext = "This is a secret message.";
            var plaintextBytes = Encoding.UTF8.GetBytes(originalPlaintext);
            var message = senderSession.Encrypt(plaintextBytes);

            using var receiverSession = new SenderKeySession(_sessionKey);

            // Act
            var decryptedPlaintextBytes = receiverSession.Decrypt(message);
            var decryptedPlaintext = Encoding.UTF8.GetString(decryptedPlaintextBytes);

            // Assert
            decryptedPlaintext.Should().Be(originalPlaintext);
        }

        [Test]
        public void Decrypt_OutOfOrderMessage_Succeeds()
        {
            // Arrange
            using var senderSession = new SenderKeySession(_sessionKey);
            var plaintext1 = "first message";
            var plaintext2 = "second message";

            var message1 = senderSession.Encrypt(Encoding.UTF8.GetBytes(plaintext1));
            var message2 = senderSession.Encrypt(Encoding.UTF8.GetBytes(plaintext2));

            using var receiverSession = new SenderKeySession(_sessionKey);

            // Act
            var decrypted2 = receiverSession.Decrypt(message2);
            var decrypted1 = receiverSession.Decrypt(message1);

            // Assert
            Encoding.UTF8.GetString(decrypted1).Should().Be(plaintext1);
            Encoding.UTF8.GetString(decrypted2).Should().Be(plaintext2);
        }

        [Test]
        public void Decrypt_WithInvalidSignature_ThrowsException()
        {
            // Arrange
            using var senderSession = new SenderKeySession(_sessionKey);
            var plaintext = Encoding.UTF8.GetBytes("a message that will be tampered with");
            var senderKeyMessage = senderSession.Encrypt(plaintext);

            // Tamper with the signature
            senderKeyMessage.Signature![0] ^= 0xFF; // Flip the first byte

            using var receiverSession = new SenderKeySession(_sessionKey);

            // Act & Assert
            Assert.Throws<CryptographicException>(() => receiverSession.Decrypt(senderKeyMessage));
        }

        [Test]
        public void Decrypt_MessageFromDifferentContext_ThrowsException()
        {
            // Arrange
            var groupA_Context = Encoding.UTF8.GetBytes("Group A ID");
            var groupB_Context = Encoding.UTF8.GetBytes("Group B ID");

            // Both sessions share the same underlying key, but have different contexts
            using var senderSession = new SenderKeySession(_sessionKey, groupA_Context);
            using var receiverSession = new SenderKeySession(_sessionKey, groupB_Context);

            var plaintext = Encoding.UTF8.GetBytes("This message is for Group A only.");
            var message = senderSession.Encrypt(plaintext);

            // Act & Assert
            // Attempting to decrypt a message from Group A in the context of Group B should fail.
            Assert.Throws<CryptographicException>(() => receiverSession.Decrypt(message));
        }

        [Test]
        public void SaveState_And_LoadState_RestoresSessionCorrectly()
        {
            // Arrange
            var context = Encoding.UTF8.GetBytes("persistent-group");
            using var senderSession = new SenderKeySession(_sessionKey, context);
            var plaintext1 = "message before save";
            var message1 = senderSession.Encrypt(Encoding.UTF8.GetBytes(plaintext1));

            // Act: Save the state
            var savedState = senderSession.SaveState();

            // Create a new session from the saved state
            using var loadedSenderSession = SenderKeySession.LoadState(savedState);

            // Send another message with the loaded session
            var plaintext2 = "message after load";
            var message2 = loadedSenderSession.Encrypt(Encoding.UTF8.GetBytes(plaintext2));

            // Assert: A new receiver session should be able to decrypt both messages
            using var receiverSession = new SenderKeySession(_sessionKey, context);

            // Decrypt the first message (sent before saving)
            var decrypted1 = receiverSession.Decrypt(message1);
            Encoding.UTF8.GetString(decrypted1).Should().Be(plaintext1);

            // Decrypt the second message (sent after loading)
            var decrypted2 = receiverSession.Decrypt(message2);
            Encoding.UTF8.GetString(decrypted2).Should().Be(plaintext2);
        }
    }
}
