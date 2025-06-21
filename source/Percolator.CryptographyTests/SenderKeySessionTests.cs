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

        [SetUp]
        public void Setup()
        {
            _sessionKey = RandomNumberGenerator.GetBytes(32);
            _context = Encoding.UTF8.GetBytes("test-context");
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
            var msg1 = _senderSession.Encrypt(Encoding.UTF8.GetBytes("message 1"));
            var msg2 = _senderSession.Encrypt(Encoding.UTF8.GetBytes("message 2"));
            var msg3 = _senderSession.Encrypt(Encoding.UTF8.GetBytes("message 3"));

            // Decrypt in reverse order
            _receiverSession.Decrypt(msg3).Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 3"));
            _receiverSession.Decrypt(msg2).Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 2"));
            _receiverSession.Decrypt(msg1).Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 1"));
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
            // Arrange
            // Create a message from the sender that is far in the future for the receiver.
            // The receiver is at iteration 0. A message with iteration > MaxSkippedMessages (1000) should be rejected.
            SenderKeyMessage? message = null;
            for (var i = 0; i <= 1001; i++)
            {
                message = _senderSession.Encrypt("ping"u8.ToArray());
            }

            // Act & Assert
            // The message is validly signed, but its iteration (1001) is too far
            // ahead of the receiver's current iteration (0).
            var ex = Assert.Throws<CryptographicException>(() => _receiverSession.Decrypt(message!));
            ex.Message.Should().Contain("exceeds the maximum number of skippable messages");
        }
    }
}
