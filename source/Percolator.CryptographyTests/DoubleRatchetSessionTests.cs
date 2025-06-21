using System;
using System.Security.Cryptography;
using AutoFixture;
using FluentAssertions;
using NUnit.Framework;
using Pecolator.Cryptography;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class DoubleRatchetSessionTests
    {
        private readonly IFixture _fixture = new Fixture();

        private byte[] CreateSharedSecret() => _fixture.Create<byte[]>();

        private ECDiffieHellman CreateKeyPair() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        [Test]
        public void Constructor_WithValidInputs_InitializesSession()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();
            var localKeyPair = CreateKeyPair();
            var remotePublicKey = CreateKeyPair().PublicKey.ExportSubjectPublicKeyInfo();

            // Act
            var session = new DoubleRatchetSession(sharedSecret, localKeyPair, remotePublicKey, SessionRole.Initiator);

            // Assert
            session.Should().NotBeNull();
            session.PublicKey.Should().BeEquivalentTo(localKeyPair.PublicKey.ExportSubjectPublicKeyInfo());
            session.SendingChainKey.Should().NotBeNullOrEmpty();
            session.ReceivingChainKey.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void Encrypt_WithValidPlaintext_ReturnsNonEmptyCiphertext()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();
            var localKeyPair = CreateKeyPair();
            var remotePublicKey = CreateKeyPair().PublicKey.ExportSubjectPublicKeyInfo();
            var session = new DoubleRatchetSession(sharedSecret, localKeyPair, remotePublicKey, SessionRole.Initiator);
            var plaintext = System.Text.Encoding.UTF8.GetBytes("Hello, world!");

            // Act
            var message = session.Encrypt(plaintext);

            // Assert
            message.Should().NotBeNull();
            message.CiphertextPayload.Should().NotBeEmpty();
            message.EphemeralPublicKey.Should().NotBeEmpty();
        }

        [Test]
        public void Encrypt_CalledTwiceWithSamePlaintext_ReturnsDifferentCiphertexts()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();
            var localKeyPair = CreateKeyPair();
            var remotePublicKey = CreateKeyPair().PublicKey.ExportSubjectPublicKeyInfo();
            var session = new DoubleRatchetSession(sharedSecret, localKeyPair, remotePublicKey, SessionRole.Initiator);
            var plaintext = System.Text.Encoding.UTF8.GetBytes("You can't step in the same river twice.");

            // Act
            var message1 = session.Encrypt(plaintext);
            var message2 = session.Encrypt(plaintext);

            // Assert
            message1.CiphertextPayload.Should().NotBeEquivalentTo(message2.CiphertextPayload);
        }

        [Test]
        public void EncryptDecrypt_WithTwoSessions_ReturnsOriginalPlaintext()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();
            var aliceInitialKeyPair = CreateKeyPair();
            var bobInitialKeyPair = CreateKeyPair();

            var sessionAlice = new DoubleRatchetSession(sharedSecret, aliceInitialKeyPair, bobInitialKeyPair.PublicKey.ExportSubjectPublicKeyInfo(), SessionRole.Initiator);
            var sessionBob = new DoubleRatchetSession(sharedSecret, bobInitialKeyPair, aliceInitialKeyPair.PublicKey.ExportSubjectPublicKeyInfo(), SessionRole.Responder);

            var plaintext = System.Text.Encoding.UTF8.GetBytes("Message from Alice to Bob");

            // Act
            var message = sessionAlice.Encrypt(plaintext);
            var decryptedText = sessionBob.Decrypt(message);

            // Assert
            decryptedText.Should().BeEquivalentTo(plaintext);
        }

        [Test]
        public void Decrypt_WithNewEphemeralKey_UpdatesReceivingChainKey()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();
            var aliceInitialKeyPair = CreateKeyPair();
            var bobInitialKeyPair = CreateKeyPair();

            var sessionAlice = new DoubleRatchetSession(sharedSecret, aliceInitialKeyPair, bobInitialKeyPair.PublicKey.ExportSubjectPublicKeyInfo(), SessionRole.Initiator);
            var sessionBob = new DoubleRatchetSession(sharedSecret, bobInitialKeyPair, aliceInitialKeyPair.PublicKey.ExportSubjectPublicKeyInfo(), SessionRole.Responder);

            var initialReceivingKey = sessionBob.ReceivingChainKey.ToArray(); // Capture initial state
            var plaintext = System.Text.Encoding.UTF8.GetBytes("Hello, new key!");

            // Act
            var message = sessionAlice.Encrypt(plaintext);
            sessionBob.Decrypt(message);
            var newReceivingKey = sessionBob.ReceivingChainKey;

            // Assert
            newReceivingKey.Should().NotBeEquivalentTo(initialReceivingKey);
        }

        [Test]
        public void Decrypt_WhenReceivingMessagesOutOfOrder_ShouldSucceed()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();
            var aliceInitialKeyPair = CreateKeyPair();
            var bobInitialKeyPair = CreateKeyPair();

            var sessionAlice = new DoubleRatchetSession(sharedSecret, aliceInitialKeyPair, bobInitialKeyPair.PublicKey.ExportSubjectPublicKeyInfo(), SessionRole.Initiator);
            var sessionBob = new DoubleRatchetSession(sharedSecret, bobInitialKeyPair, aliceInitialKeyPair.PublicKey.ExportSubjectPublicKeyInfo(), SessionRole.Responder);

            var plaintext1 = System.Text.Encoding.UTF8.GetBytes("Message 1");
            var plaintext2 = System.Text.Encoding.UTF8.GetBytes("Message 2");
            var plaintext3 = System.Text.Encoding.UTF8.GetBytes("Message 3");

            var message1 = sessionAlice.Encrypt(plaintext1);
            var message2 = sessionAlice.Encrypt(plaintext2);
            var message3 = sessionAlice.Encrypt(plaintext3);

            // Act & Assert
            // Receive message 3 first (skipping 1 and 2)
            var decryptedText3 = sessionBob.Decrypt(message3);
            decryptedText3.Should().BeEquivalentTo(plaintext3);

            // Receive message 2 next (from cache)
            var decryptedText2 = sessionBob.Decrypt(message2);
            decryptedText2.Should().BeEquivalentTo(plaintext2);

            // Receive message 1 last (from cache)
            var decryptedText1 = sessionBob.Decrypt(message1);
            decryptedText1.Should().BeEquivalentTo(plaintext1);
        }

        [Test]
        public void Decrypt_WithTamperedCiphertext_ShouldThrow()
        {
            // Arrange
            var sharedSecret = CreateSharedSecret();
            var aliceInitialKeyPair = CreateKeyPair();
            var bobInitialKeyPair = CreateKeyPair();

            var sessionAlice = new DoubleRatchetSession(sharedSecret, aliceInitialKeyPair, bobInitialKeyPair.PublicKey.ExportSubjectPublicKeyInfo(), SessionRole.Initiator);
            var sessionBob = new DoubleRatchetSession(sharedSecret, bobInitialKeyPair, aliceInitialKeyPair.PublicKey.ExportSubjectPublicKeyInfo(), SessionRole.Responder);

            var plaintext = System.Text.Encoding.UTF8.GetBytes("This is a secret message.");
            var message = sessionAlice.Encrypt(plaintext);

            // Act: Tamper with the ciphertext by flipping a bit
            var tamperedPayload = message.CiphertextPayload.ToArray();
            tamperedPayload[tamperedPayload.Length - 5] ^= 0x01; // Flip a bit somewhere in the middle
            var tamperedMessage = new RatchetMessage(message.EphemeralPublicKey, tamperedPayload);

            // Assert
            Action act = () => sessionBob.Decrypt(tamperedMessage);
            act.Should().Throw<InvalidMessageOrderException>()
               .WithInnerException<System.Security.Cryptography.AuthenticationTagMismatchException>();
        }
    }
}
