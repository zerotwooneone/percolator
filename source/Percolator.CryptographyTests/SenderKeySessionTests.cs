using FluentAssertions;
using NUnit.Framework;
using Pecolator.Cryptography;
using System.Security.Cryptography;
using System.Text;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class SenderKeySessionTests
    {
        private byte[] _sessionKey = null!;
        private byte[] _context = null!;

        [SetUp]
        public void Setup()
        {
            _sessionKey = RandomNumberGenerator.GetBytes(32);
            _context = Encoding.UTF8.GetBytes("test-context");
        }

        [Test]
        public void Constructor_WithValidKey_InitializesSession()
        {
            // Arrange & Act
            using var session = new SenderKeySession(_sessionKey, _context);

            // Assert
            session.Should().NotBeNull();
        }

        [Test]
        public void Encrypt_ThenDecrypt_ReturnsOriginalPlaintext()
        {
            // Arrange
            using var senderSession = new SenderKeySession(_sessionKey, _context);
            using var receiverSession = new SenderKeySession(_sessionKey, _context);
            var plaintext = "Hello, world!";

            // Act
            var encryptedMessage = senderSession.Encrypt(Encoding.UTF8.GetBytes(plaintext));
            var decryptedBytes = receiverSession.Decrypt(encryptedMessage);

            // Assert
            Encoding.UTF8.GetString(decryptedBytes).Should().Be(plaintext);
        }

        [Test]
        public void Decrypt_OutOfOrderMessage_Succeeds()
        {
            // Arrange
            using var senderSession = new SenderKeySession(_sessionKey, _context);
            using var receiverSession = new SenderKeySession(_sessionKey, _context);

            var message1 = senderSession.Encrypt(Encoding.UTF8.GetBytes("first"));
            var message2 = senderSession.Encrypt(Encoding.UTF8.GetBytes("second"));
            var message3 = senderSession.Encrypt(Encoding.UTF8.GetBytes("third"));

            // Act & Assert: Decrypt in a different order
            Encoding.UTF8.GetString(receiverSession.Decrypt(message3)).Should().Be("third");
            Encoding.UTF8.GetString(receiverSession.Decrypt(message1)).Should().Be("first");
            Encoding.UTF8.GetString(receiverSession.Decrypt(message2)).Should().Be("second");
        }

        [Test]
        public void Decrypt_MessageFromDifferentContext_ThrowsException()
        {
            // Arrange
            var context1 = Encoding.UTF8.GetBytes("group1");
            var context2 = Encoding.UTF8.GetBytes("group2");
            var sessionKey = RandomNumberGenerator.GetBytes(32);

            using var session1 = new SenderKeySession(sessionKey, context1);
            using var session2 = new SenderKeySession(sessionKey, context2);

            var message = session1.Encrypt(Encoding.UTF8.GetBytes("secret message"));

            // Act
            Action act = () => session2.Decrypt(message);

            // Assert
            act.Should().Throw<CryptographicException>().WithMessage("Invalid signature.");
        }

        [Test]
        public void SaveState_And_LoadState_RestoresSessionCorrectly()
        {
            // Arrange: Create a sender and a long-lived receiver session
            using var senderSession = new SenderKeySession(_sessionKey, _context);
            using var receiverSession = new SenderKeySession(_sessionKey, _context);

            // Act 1: Send a message, save state, and verify receiver can decrypt
            var message1 = senderSession.Encrypt(Encoding.UTF8.GetBytes("message before save"));
            var state = senderSession.SaveState();
            Encoding.UTF8.GetString(receiverSession.Decrypt(message1)).Should().Be("message before save");

            // Act 2: Load the state into a new session and send another message
            using var loadedSession = SenderKeySession.LoadState(state);
            var message2 = loadedSession.Encrypt(Encoding.UTF8.GetBytes("message after load"));

            // Assert: The original receiver session can decrypt the message from the loaded sender session
            Encoding.UTF8.GetString(receiverSession.Decrypt(message2)).Should().Be("message after load");
        }
    }
}
