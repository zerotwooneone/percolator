using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using System.Security.Cryptography;
using System.Text;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class DoubleRatchetSessionTests
    {
        private ECDiffieHellman _aliceIdentity;
        private ECDiffieHellman _bobIdentity;
        private DoubleRatchetSession? _aliceSession;
        private DoubleRatchetSession? _bobSession;
        private byte[] _masterKey;

        [SetUp]
        public void Setup()
        {
            _aliceIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _bobIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _masterKey = RandomNumberGenerator.GetBytes(32);
        }

        [TearDown]
        public void TearDown()
        {
            _aliceIdentity?.Dispose();
            _bobIdentity?.Dispose();
            _aliceSession?.Dispose();
            _bobSession?.Dispose();
        }

        private void InitializeSessions()
        {
            var bobRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _aliceSession = new DoubleRatchetSession(_aliceIdentity, _bobIdentity.PublicKey.ExportSubjectPublicKeyInfo(), bobRatchetKey.PublicKey.ExportSubjectPublicKeyInfo());
            _bobSession = new DoubleRatchetSession(_bobIdentity, bobRatchetKey, _aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo());
        }

        [Test]
        public void Encrypt_And_Decrypt_Succeeds_Simple()
        {
            InitializeSessions();
            var plaintext = Encoding.UTF8.GetBytes("Hello Bob!");
            var message = _aliceSession!.Encrypt(plaintext);
            var decrypted = _bobSession!.Decrypt(message);
            decrypted.Should().BeEquivalentTo(plaintext);
        }

        [Test]
        public void Encrypt_And_Decrypt_Succeeds_After_Ratchet()
        {
            InitializeSessions();
            var plaintext1 = Encoding.UTF8.GetBytes("Hello Bob, this is Alice.");
            var message1 = _aliceSession!.Encrypt(plaintext1);
            var decrypted1 = _bobSession!.Decrypt(message1);
            decrypted1.Should().BeEquivalentTo(plaintext1);

            var plaintext2 = Encoding.UTF8.GetBytes("Hello Alice, this is Bob.");
            var message2 = _bobSession!.Encrypt(plaintext2);
            var decrypted2 = _aliceSession!.Decrypt(message2);
            decrypted2.Should().BeEquivalentTo(plaintext2);
        }

        [Test]
        public void SaveState_And_LoadState_RestoresSessionCorrectly()
        {
            InitializeSessions();
            var plaintext = "Hello, Bob!"u8.ToArray();
            var message = _aliceSession!.Encrypt(plaintext);
            _bobSession!.Decrypt(message);

            var savedState = _bobSession.SaveState(_masterKey);
            using var loadedBobSession = DoubleRatchetSession.LoadState(_masterKey, savedState);

            var response = loadedBobSession.Encrypt("Hello, Alice!"u8.ToArray());
            var decryptedResponse = _aliceSession.Decrypt(response);

            Encoding.UTF8.GetString(decryptedResponse).Should().Be("Hello, Alice!");
        }

        [Test]
        public void LoadState_WithWrongKey_Throws()
        {
            InitializeSessions();
            var savedState = _aliceSession!.SaveState(_masterKey);
            var wrongKey = RandomNumberGenerator.GetBytes(32);

            Assert.Catch<CryptographicException>(() => DoubleRatchetSession.LoadState(wrongKey, savedState));
        }

        [Test]
        public void LoadState_WithTamperedState_Throws()
        {
            InitializeSessions();
            var savedState = _aliceSession!.SaveState(_masterKey);
            savedState[0] ^= 0xff; // Tamper with the state

            Assert.Catch<CryptographicException>(() => DoubleRatchetSession.LoadState(_masterKey, savedState));
        }

        [Test]
        public void Decrypt_WithSkippedMessages_Succeeds()
        {
            InitializeSessions();
            var msg1 = _aliceSession!.Encrypt(Encoding.UTF8.GetBytes("message 1"));
            var msg2 = _aliceSession!.Encrypt(Encoding.UTF8.GetBytes("message 2"));
            var msg3 = _aliceSession!.Encrypt(Encoding.UTF8.GetBytes("message 3"));

            // Decrypt in reverse order
            _bobSession!.Decrypt(msg3).Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 3"));
            _bobSession!.Decrypt(msg2).Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 2"));
            _bobSession!.Decrypt(msg1).Should().BeEquivalentTo(Encoding.UTF8.GetBytes("message 1"));
        }
    }
}
