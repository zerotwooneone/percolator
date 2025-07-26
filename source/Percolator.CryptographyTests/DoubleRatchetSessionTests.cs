using FluentAssertions;
using Percolator.Cryptography;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoubleRatchetSessionState = Percolator.Cryptography.DoubleRatchetSession.DoubleRatchetSessionState;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class DoubleRatchetSessionTests
    {
        private ECDiffieHellman _aliceIdentity;
        private ECDiffieHellman _bobIdentity;
        private ECDiffieHellman _bobRatchetKey;
        private DoubleRatchetSession _aliceSession;
        private DoubleRatchetSession _bobSession;

        [SetUp]
        public void Setup()
        {
            _aliceIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _bobIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _bobRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            // Simulate a secure key exchange (e.g., X3DH) to get a shared secret.
            var sharedSecret = new SharedSecret(_aliceIdentity.DeriveKeyMaterial(_bobIdentity.PublicKey));

            _aliceSession = DoubleRatchetSession.AsInitiator(
                sharedSecret,
                new RatchetIdentityKey(_bobIdentity.PublicKey.ExportSubjectPublicKeyInfo()),
                new RatchetEphemeralKey(_bobRatchetKey.PublicKey.ExportSubjectPublicKeyInfo())
            );

            _bobSession = DoubleRatchetSession.AsResponder(
                sharedSecret,
                new RatchetIdentityKey(_aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo()),
                _bobRatchetKey
            );
        }

        [TearDown]
        public void TearDown()
        {
            // Dispose of all keys after each test.
            _aliceSession.Dispose();
            _bobSession.Dispose();
            _aliceIdentity.Dispose();
            _bobIdentity.Dispose();
            _bobRatchetKey.Dispose();
        }

        [Test]
        public void Encrypt_And_Decrypt_Succeeds_Simple()
        {
            var plaintext = Encoding.UTF8.GetBytes("Hello Bob!");
            var message = _aliceSession.Encrypt(new Plaintext(plaintext));
            var decrypted = _bobSession.Decrypt(message);
            decrypted.Value.Should().BeEquivalentTo(plaintext);
        }

        [Test]
        public void Encrypt_And_Decrypt_Succeeds_After_Ratchet()
        {
            var plaintext1 = Encoding.UTF8.GetBytes("Hello Bob, this is Alice.");
            var message1 = _aliceSession.Encrypt(new Plaintext(plaintext1));
            var decrypted1 = _bobSession.Decrypt(message1);
            decrypted1.Value.Should().BeEquivalentTo(plaintext1);

            var plaintext2 = Encoding.UTF8.GetBytes("Hello Alice, this is Bob.");
            var message2 = _bobSession.Encrypt(new Plaintext(plaintext2));
            var decrypted2 = _aliceSession.Decrypt(message2);
            decrypted2.Value.Should().BeEquivalentTo(plaintext2);
        }

        [Test]
        public void GetState_And_Restore_RestoresSessionCorrectly()
        {
            var plaintext = "Hello, Bob!"u8.ToArray();
            var message = _aliceSession.Encrypt(new Plaintext(plaintext));
            _bobSession.Decrypt(message);

            var state = _bobSession.GetState();
            // Simulate serializing and deserializing the state
            var serializedState = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            var deserializedState = JsonSerializer.Deserialize<DoubleRatchetSessionState>(serializedState)!;
            using var loadedBobSession = new DoubleRatchetSession(deserializedState);

            var response = loadedBobSession.Encrypt(new Plaintext("Hello, Alice!"u8.ToArray()));
            var decryptedResponse = _aliceSession.Decrypt(response);

            Encoding.UTF8.GetString(decryptedResponse.Value).Should().Be("Hello, Alice!");
        }

        [Test]
        public void Decrypt_WithSkippedMessages_Succeeds()
        {
            var msg1 = _aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("message 1")));
            var msg2 = _aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("message 2")));
            var msg3 = _aliceSession.Encrypt(new Plaintext(Encoding.UTF8.GetBytes("message 3")));

            // Decrypt in reverse order
            _bobSession.Decrypt(msg3).Value.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 3"));
            _bobSession.Decrypt(msg2).Value.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 2"));
            _bobSession.Decrypt(msg1).Value.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 1"));
        }

        [Test]
        public void Decrypt_WithTamperedHeader_ThrowsException()
        {
            // Arrange
            var message = _aliceSession.Encrypt(new Plaintext("ping"u8.ToArray()));

            // Act: Tamper with the header after encryption
            // Extract the original header information
            var (ratchetKey, counter) = message.GetHeader();
            
            // Create a new message with a tampered counter
            var tamperedMessage = SessionRatchetMessage.Create(
                ratchetKey, 
                counter + 1, // Increment the counter to tamper with the header
                message.GetCiphertext());

            // Assert: Decryption must fail because the AD (the header) no longer matches the ciphertext.
            // We expect the specific AuthenticationTagMismatchException, which is a subclass of CryptographicException.
            Assert.Throws<System.Security.Cryptography.AuthenticationTagMismatchException>(() => _bobSession.Decrypt(tamperedMessage));
        }
    }
}
