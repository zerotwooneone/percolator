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
            // Manually create a message with a very high iteration number
            var tamperedMessage = new SenderKeyMessage
            {
                Header = new SenderKeyHeader { Iteration = 2000 },
                Ciphertext = "dummy-ciphertext"u8.ToArray()
            };

            var ex = Assert.Throws<CryptographicException>(
                () => _receiverSession.Decrypt(tamperedMessage));

            ex.Message.Should().Be("Cannot process message with iteration 2000 because it exceeds the maximum number of skippable messages (1000).");
        }
    }
}
