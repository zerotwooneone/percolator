using NUnit.Framework;
using FluentAssertions;
using System;
using System.Security.Cryptography;
using Pecolator.Cryptography;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class DoubleRatchetSessionTests
    {
        private byte[] CreateSharedSecret()
        {
            var secret = new byte[32];
            RandomNumberGenerator.Fill(secret);
            return secret;
        }

        [Test]
        public void Constructor_WhenCalled_ShouldNotThrow()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();

            // Act
            Action act = () => new DoubleRatchetSession(sharedSecret, SessionRole.Initiator);

            // Assert
            act.Should().NotThrow();
        }

        [Test]
        public void Encrypt_WithValidPlaintext_ReturnsNonEmptyCiphertext()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();
            var session = new DoubleRatchetSession(sharedSecret, SessionRole.Initiator);
            var plaintext = System.Text.Encoding.UTF8.GetBytes("Hello, world!");

            // Act
            var ciphertext = session.Encrypt(plaintext);

            // Assert
            ciphertext.Should().NotBeNull();
            ciphertext.Should().NotBeEmpty();
        }

        [Test]
        public void Encrypt_CalledTwiceWithSamePlaintext_ReturnsDifferentCiphertexts()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();
            var session = new DoubleRatchetSession(sharedSecret, SessionRole.Initiator);
            var plaintext = System.Text.Encoding.UTF8.GetBytes("You can't step in the same river twice.");

            // Act
            var ciphertext1 = session.Encrypt(plaintext);
            var ciphertext2 = session.Encrypt(plaintext);

            // Assert
            ciphertext1.Should().NotBeEquivalentTo(ciphertext2);
        }

        [Test]
        public void EncryptDecrypt_WithTwoSessions_ReturnsOriginalPlaintext()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();
            var sessionAlice = new DoubleRatchetSession(sharedSecret, SessionRole.Initiator);
            var sessionBob = new DoubleRatchetSession(sharedSecret, SessionRole.Responder);
            var plaintext = System.Text.Encoding.UTF8.GetBytes("Message from Alice to Bob");

            // Act
            var ciphertext = sessionAlice.Encrypt(plaintext);
            var decryptedText = sessionBob.Decrypt(ciphertext);

            // Assert
            decryptedText.Should().BeEquivalentTo(plaintext);
        }

        [Test]
        public void Decrypt_WhenReceivingMessagesOutOfOrder_ShouldFailForOlderMessage()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();
            var sessionAlice = new DoubleRatchetSession(sharedSecret, SessionRole.Initiator);
            var sessionBob = new DoubleRatchetSession(sharedSecret, SessionRole.Responder);
            var plaintext1 = System.Text.Encoding.UTF8.GetBytes("First message");
            var plaintext2 = System.Text.Encoding.UTF8.GetBytes("Second message");

            // Act
            var ciphertext1 = sessionAlice.Encrypt(plaintext1);
            var ciphertext2 = sessionAlice.Encrypt(plaintext2);

            // Decrypt the second message first, which should succeed.
            var decryptedText2 = sessionBob.Decrypt(ciphertext2);

            // Then, attempting to decrypt the first message should fail.
            Action act = () => sessionBob.Decrypt(ciphertext1);

            // Assert
            decryptedText2.Should().BeEquivalentTo(plaintext2);
            act.Should().Throw<InvalidMessageOrderException>();
        }
    }
}
