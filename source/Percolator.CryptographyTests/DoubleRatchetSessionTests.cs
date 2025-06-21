using System;
using System.Security.Cryptography;
using AutoFixture;
using FluentAssertions;
using NUnit.Framework;
using Pecolator.Cryptography;
using System.Text;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class DoubleRatchetSessionTests
    {
        private byte[] _sharedSecret;
        private ECDiffieHellman _aliceIdentityKey;
        private ECDiffieHellman _bobIdentityKey;
        private ECDiffieHellman _bobPreKey;

        [SetUp]
        public void Setup()
        {
            _sharedSecret = new byte[32];
            RandomNumberGenerator.Fill(_sharedSecret);
            _aliceIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _bobIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _bobPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        }

        [TearDown]
        public void Teardown()
        {
            _aliceIdentityKey?.Dispose();
            _bobIdentityKey?.Dispose();
            _bobPreKey?.Dispose();
        }

        [Test]
        public void Constructor_WithValidInputs_InitializesSession()
        {
            // Arrange
            using var bobSession = new DoubleRatchetSession(_sharedSecret, _bobIdentityKey, SessionRole.Responder, ownInitialRatchetKey: _bobPreKey);
            var bobRatchetPublicKey = bobSession.RatchetPublicKey;

            // Act
            using var aliceSession = new DoubleRatchetSession(_sharedSecret, _aliceIdentityKey, SessionRole.Initiator, bobRatchetPublicKey);

            // Assert
            aliceSession.Should().NotBeNull();
            aliceSession.IdentityPublicKey.Should().BeEquivalentTo(_aliceIdentityKey.PublicKey.ExportSubjectPublicKeyInfo());
            aliceSession.SendingChainKey.Should().NotBeNullOrEmpty();
            aliceSession.ReceivingChainKey.Should().BeNull();
        }

        [Test]
        public void Encrypt_WithValidPlaintext_ReturnsNonEmptyCiphertext()
        {
            // Arrange
            using var bobSession = new DoubleRatchetSession(_sharedSecret, _bobIdentityKey, SessionRole.Responder, ownInitialRatchetKey: _bobPreKey);
            var bobRatchetPublicKey = bobSession.RatchetPublicKey;
            using var aliceSession = new DoubleRatchetSession(_sharedSecret, _aliceIdentityKey, SessionRole.Initiator, bobRatchetPublicKey);

            // Act
            var message = aliceSession.Encrypt(Encoding.UTF8.GetBytes("test"));

            // Assert
            message.CiphertextPayload.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void Encrypt_CalledTwiceWithSamePlaintext_ReturnsDifferentCiphertexts()
        {
            // Arrange
            using var bobSession = new DoubleRatchetSession(_sharedSecret, _bobIdentityKey, SessionRole.Responder, ownInitialRatchetKey: _bobPreKey);
            var bobRatchetPublicKey = bobSession.RatchetPublicKey;
            using var aliceSession = new DoubleRatchetSession(_sharedSecret, _aliceIdentityKey, SessionRole.Initiator, bobRatchetPublicKey);
            var plaintext = Encoding.UTF8.GetBytes("test");

            // Act
            var message1 = aliceSession.Encrypt(plaintext);
            var message2 = aliceSession.Encrypt(plaintext);

            // Assert
            message1.CiphertextPayload.Should().NotBeEquivalentTo(message2.CiphertextPayload);
        }

        [Test]
        public void EncryptDecrypt_WithTwoSessions_ReturnsOriginalPlaintext()
        {
            // Arrange
            using var bobSession = new DoubleRatchetSession(_sharedSecret, _bobIdentityKey, SessionRole.Responder, ownInitialRatchetKey: _bobPreKey);
            var bobRatchetPublicKey = bobSession.RatchetPublicKey;
            using var aliceSession = new DoubleRatchetSession(_sharedSecret, _aliceIdentityKey, SessionRole.Initiator, bobRatchetPublicKey);
            var plaintext = "Hello, Bob!";

            // Act
            var message = aliceSession.Encrypt(Encoding.UTF8.GetBytes(plaintext));
            var decryptedBytes = bobSession.Decrypt(message);
            var decryptedText = Encoding.UTF8.GetString(decryptedBytes);

            // Assert
            decryptedText.Should().Be(plaintext);
        }

        [Test]
        public void Decrypt_WithNewEphemeralKey_UpdatesReceivingChainKey()
        {
            // Arrange
            using var bobSession = new DoubleRatchetSession(_sharedSecret, _bobIdentityKey, SessionRole.Responder, ownInitialRatchetKey: _bobPreKey);
            var bobRatchetPublicKey = bobSession.RatchetPublicKey;
            using var aliceSession = new DoubleRatchetSession(_sharedSecret, _aliceIdentityKey, SessionRole.Initiator, bobRatchetPublicKey);
            bobSession.ReceivingChainKey.Should().BeNull();

            // Act
            var message = aliceSession.Encrypt(Encoding.UTF8.GetBytes("first message"));
            bobSession.Decrypt(message);

            // Assert
            bobSession.ReceivingChainKey.Should().NotBeNull();
        }

        [Test]
        public void Security_ImportInvalidPublicKey_ThrowsException()
        {
            // Arrange
            // This is a known invalid public key for the nistP256 curve (point is not on the curve).
            // It is correctly formatted as an X.509 SubjectPublicKeyInfo.
            var invalidPublicKeyBytes = Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/1+t8y0tLg3qGSs3fR018R3g2cNa3/zI/pECm8bYm24D4sYf6dZ1ZgE8tVAvCg2mUDV/a51Zy1s8Oa6hZw==");
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            // Act & Assert
            Assert.Throws<CryptographicException>(() => ecdh.ImportSubjectPublicKeyInfo(invalidPublicKeyBytes, out _));
        }

        [Test]
        public void Decrypt_OutOfOrderMessage_Succeeds()
        {
            // Arrange
            using var bobSession = new DoubleRatchetSession(_sharedSecret, _bobIdentityKey, SessionRole.Responder, ownInitialRatchetKey: _bobPreKey);
            var bobRatchetPublicKey = bobSession.RatchetPublicKey;
            using var aliceSession = new DoubleRatchetSession(_sharedSecret, _aliceIdentityKey, SessionRole.Initiator, bobRatchetPublicKey);

            var message1 = aliceSession.Encrypt(Encoding.UTF8.GetBytes("message 1"));
            var message2 = aliceSession.Encrypt(Encoding.UTF8.GetBytes("message 2"));
            var message3 = aliceSession.Encrypt(Encoding.UTF8.GetBytes("message 3"));

            // Act & Assert
            var decrypted1 = Encoding.UTF8.GetString(bobSession.Decrypt(message1));
            var decrypted3 = Encoding.UTF8.GetString(bobSession.Decrypt(message3));
            var decrypted2 = Encoding.UTF8.GetString(bobSession.Decrypt(message2));

            decrypted1.Should().Be("message 1");
            decrypted2.Should().Be("message 2");
            decrypted3.Should().Be("message 3");
        }

        [Test]
        public void Decrypt_WithTamperedCiphertext_Throws()
        {
            // Arrange
            using var bobSession = new DoubleRatchetSession(_sharedSecret, _bobIdentityKey, SessionRole.Responder, ownInitialRatchetKey: _bobPreKey);
            var bobRatchetPublicKey = bobSession.RatchetPublicKey;
            using var aliceSession = new DoubleRatchetSession(_sharedSecret, _aliceIdentityKey, SessionRole.Initiator, bobRatchetPublicKey);
            var message = aliceSession.Encrypt(Encoding.UTF8.GetBytes("test"));
            message.CiphertextPayload[10]++; // Tamper with the ciphertext

            // Act
            Action act = () => bobSession.Decrypt(message);

            // Assert
            act.Should().Throw<InvalidMessageOrderException>().WithInnerException<AuthenticationTagMismatchException>();
        }

        [Test]
        public void SaveState_And_LoadState_RestoresSessionCorrectly()
        {
            // Arrange: Initial handshake and first message
            var sharedSecret = _aliceIdentityKey.DeriveKeyMaterial(_bobPreKey.PublicKey);
            var aliceSession = new DoubleRatchetSession(sharedSecret, _aliceIdentityKey, SessionRole.Initiator, _bobPreKey.PublicKey.ExportSubjectPublicKeyInfo());
            var bobSession = new DoubleRatchetSession(sharedSecret, _bobIdentityKey, SessionRole.Responder, ownInitialRatchetKey: _bobPreKey);

            var plaintext1 = "message before save";
            var message1 = aliceSession.Encrypt(Encoding.UTF8.GetBytes(plaintext1));

            // Act: Alice saves her state, creates a new session from it, and sends another message
            var aliceState = aliceSession.SaveState();
            aliceSession.Dispose(); // Dispose the old session

            var loadedAliceSession = DoubleRatchetSession.LoadState(aliceState, _aliceIdentityKey);
            var plaintext2 = "message after load";
            var message2 = loadedAliceSession.Encrypt(Encoding.UTF8.GetBytes(plaintext2));

            // Assert: Bob should be able to decrypt both messages seamlessly
            var decrypted1 = bobSession.Decrypt(message1);
            Encoding.UTF8.GetString(decrypted1).Should().Be(plaintext1);

            var decrypted2 = bobSession.Decrypt(message2);
            Encoding.UTF8.GetString(decrypted2).Should().Be(plaintext2);
        }
    }
}
