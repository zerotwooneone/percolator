using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using System.Security.Cryptography;
using System.Text;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class SenderKeySessionTests
    {
        private byte[] _sessionKey;
        private byte[] _context;
        private SenderKeySession _senderSession;
        private SenderKeySession _receiverSession;
        private byte[] _masterKey;

        [SetUp]
        public void Setup()
        {
            _sessionKey = RandomNumberGenerator.GetBytes(32);
            _context = Encoding.UTF8.GetBytes("test-context");
            _masterKey = RandomNumberGenerator.GetBytes(32);
            _senderSession = new SenderKeySession(_sessionKey, _context);
            _receiverSession = new SenderKeySession(_sessionKey, _context);
        }

        [TearDown]
        public void TearDown()
        {
            _senderSession?.Dispose();
            _receiverSession?.Dispose();
        }

        [Test]
        public void Encrypt_And_Decrypt_Succeeds()
        {
            var plaintext = Encoding.UTF8.GetBytes("This is a group message.");
            var message = _senderSession.Encrypt(plaintext);
            var decrypted = _receiverSession.Decrypt(message);
            decrypted.Should().BeEquivalentTo(plaintext);
        }

        [Test]
        public void Decrypt_WithInvalidSignature_ThrowsCryptographicException()
        {
            var plaintext = Encoding.UTF8.GetBytes("This is a group message.");
            var message = _senderSession.Encrypt(plaintext);
            message.Signature[0] ^= 0xff; // Tamper with signature

            Assert.Throws<CryptographicException>(() => _receiverSession.Decrypt(message));
        }

        [Test]
        public void Decrypt_WithSkippedMessages_Succeeds()
        {
            var msg1 = _senderSession.Encrypt("one"u8.ToArray());
            var msg2 = _senderSession.Encrypt("two"u8.ToArray());
            var msg3 = _senderSession.Encrypt("three"u8.ToArray());

            _receiverSession.Decrypt(msg3).Should().BeEquivalentTo("three"u8.ToArray());
            _receiverSession.Decrypt(msg1).Should().BeEquivalentTo("one"u8.ToArray());
            _receiverSession.Decrypt(msg2).Should().BeEquivalentTo("two"u8.ToArray());
        }

        [Test]
        public void Decrypt_OldMessage_ThrowsCryptographicException()
        {
            var msg1 = _senderSession.Encrypt(Encoding.UTF8.GetBytes("message 1"));
            _receiverSession.Decrypt(msg1); // Decrypt first message

            // Try to decrypt it again
            Assert.Throws<CryptographicException>(() => _receiverSession.Decrypt(msg1));
        }

        [Test]
        public void Decrypt_WithTooManySkippedMessages_ThrowsException()
        {
            var message = _senderSession.Encrypt("ping"u8.ToArray());

            // Manually create a message with a very high iteration number
            // This requires creating a valid signature for the tampered data.
            var tamperedHeader = new SenderKeyHeader { Iteration = 2000 };
            var tamperedAd = tamperedHeader.ToAssociatedData(_context);
            var tamperedSignature = SignData(tamperedAd, message.Ciphertext);

            var tamperedMessage = new SenderKeyMessage
            {
                Header = tamperedHeader,
                Ciphertext = message.Ciphertext,
                Signature = tamperedSignature
            };

            Assert.Throws<CryptographicException>(
                () => _receiverSession.Decrypt(tamperedMessage),
                "Cannot process message with iteration 2000 because it exceeds the maximum number of skippable messages (1000)."
            );
        }

        [Test]
        public void Decrypt_WithTamperedIteration_ThrowsException()
        {
            // Arrange
            var message = _senderSession.Encrypt("ping"u8.ToArray());

            // Act: Tamper with the iteration in the header
            message.Header.Iteration++;

            // Assert: Decryption must fail. The signature verification should catch this.
            var ex = Assert.Throws<CryptographicException>(() => _receiverSession.Decrypt(message));
            ex.Message.Should().Be("Invalid signature.");
        }

        // Helper to sign data with the sender's private key, needed for the DoS test.
        private byte[] SignData(byte[] associatedData, byte[] ciphertext)
        {
            // To test the DoS scenario, we need to re-sign a message with a tampered header.
            // We can get the private signing key from the session's state.
            var senderState = _senderSession.GetState();
            using var signingKey = ECDsa.Create();
            signingKey.ImportECPrivateKey(senderState.SigningKeyPrivate, out _);

            var dataToSign = new byte[associatedData.Length + ciphertext.Length];
            associatedData.CopyTo(dataToSign, 0);
            ciphertext.CopyTo(dataToSign, associatedData.Length);

            return signingKey.SignData(dataToSign, HashAlgorithmName.SHA256);
        }
    }
}
