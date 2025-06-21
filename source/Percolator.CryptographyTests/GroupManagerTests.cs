using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using System.Security.Cryptography;
using System.Text;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class GroupManagerTests
    {
        private ECDiffieHellman _aliceIdentityKey;
        private ECDiffieHellman _bobIdentityKey;
        private ECDiffieHellman _bobPreKey;

        [SetUp]
        public void Setup()
        {
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
        public void CreateGroup_And_SendReceiveMessage_Succeeds()
        {
            // Arrange: Alice and Bob establish a secure channel.
            var aliceManager = new GroupManager();
            var bobManager = new GroupManager();

            // Alice (initiator) creates a session with Bob using his public identity and public ratchet key.
            using var aliceToBobSession = new DoubleRatchetSession(_aliceIdentityKey, _bobIdentityKey.PublicKey.ExportSubjectPublicKeyInfo(), _bobPreKey.PublicKey.ExportSubjectPublicKeyInfo());
            
            // Bob (responder) creates a session with Alice using his private ratchet key.
            using var bobToAliceSession = new DoubleRatchetSession(_bobIdentityKey, _bobPreKey, _aliceIdentityKey.PublicKey.ExportSubjectPublicKeyInfo());

            // Act: Alice creates a group and invites Bob.
            var (invitation, aliceGroupSession) = aliceManager.CreateGroupAndInvitation(aliceToBobSession);
            var bobGroupSession = bobManager.AcceptInvitation(bobToAliceSession, invitation);

            // Alice sends a message to the group.
            var plaintext = "Welcome to the group!";
            var senderKeyMessage = aliceGroupSession!.Encrypt(Encoding.UTF8.GetBytes(plaintext));

            // Bob receives and decrypts the message.
            var decryptedBytes = bobGroupSession!.Decrypt(senderKeyMessage);
            var decryptedText = Encoding.UTF8.GetString(decryptedBytes);

            // Assert
            decryptedText.Should().Be(plaintext);
        }
    }
}
