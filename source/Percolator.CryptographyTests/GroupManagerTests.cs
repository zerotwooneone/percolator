using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests
{
    public class GroupManagerTests
    {
        private ECDiffieHellman _aliceIdentity, _bobIdentity, _carolIdentity;
        private ECDiffieHellman _aliceRatchet, _bobRatchet, _carolRatchet;

        [SetUp]
        public void Setup()
        {
            _aliceIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _bobIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _carolIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            
            _aliceRatchet = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _bobRatchet = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _carolRatchet = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        }

        [TearDown]
        public void Teardown()
        {
            _aliceIdentity.Dispose();
            _bobIdentity.Dispose();
            _carolIdentity.Dispose();
            _aliceRatchet.Dispose();
            _bobRatchet.Dispose();
            _carolRatchet.Dispose();
        }

        [Test]
        public void CreateGroup_And_SendReceiveMessage_Succeeds()
        {
            // Arrange
            var aliceManager = new GroupManager();
            var aliceToBobSession = new DoubleRatchetSession(_aliceIdentity, _bobIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _bobRatchet.PublicKey.ExportSubjectPublicKeyInfo());
            var bobToAliceSession = new DoubleRatchetSession(_bobIdentity, _bobRatchet, _aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo());

            // Act
            var invitation = aliceManager.CreateInvitation("bob", aliceToBobSession);
            var bobManager = GroupManager.AcceptInvitation(bobToAliceSession, invitation);

            var plaintext = "Welcome to the group!";
            var senderKeyMessage = aliceManager.GroupSession.Encrypt(Encoding.UTF8.GetBytes(plaintext));

            var decryptedBytes = bobManager.GroupSession.Decrypt(senderKeyMessage);
            var decryptedText = Encoding.UTF8.GetString(decryptedBytes);

            // Assert
            decryptedText.Should().Be(plaintext);
        }

        [Test]
        public void RemoveMember_PreventsDecryptionByRemovedMember()
        {
            // Arrange: Alice creates a group and invites Bob and Carol.
            var aliceManager = new GroupManager();
            var aliceToBob = new DoubleRatchetSession(_aliceIdentity, _bobIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _bobRatchet.PublicKey.ExportSubjectPublicKeyInfo());
            var bobToAlice = new DoubleRatchetSession(_bobIdentity, _bobRatchet, _aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo());
            var aliceToCarol = new DoubleRatchetSession(_aliceIdentity, _carolIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _carolRatchet.PublicKey.ExportSubjectPublicKeyInfo());
            var carolToAlice = new DoubleRatchetSession(_carolIdentity, _carolRatchet, _aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo());

            var bobInvitation = aliceManager.CreateInvitation("bob", aliceToBob);
            var carolInvitation = aliceManager.CreateInvitation("carol", aliceToCarol);

            var bobManager = GroupManager.AcceptInvitation(bobToAlice, bobInvitation);
            var carolManager = GroupManager.AcceptInvitation(carolToAlice, carolInvitation);
            var carolOldGroupSession = carolManager.GroupSession; // Save Carol's session before she's removed.

            // Act: Alice removes Carol from the group.
            var rekeyMessages = aliceManager.RemoveMember("carol");

            // Bob processes the re-key message.
            bobManager.ProcessRekeyMessage(bobToAlice, rekeyMessages["bob"]);

            // Alice sends a new message to the group after re-keying.
            var messageAfterRemoval = aliceManager.GroupSession.Encrypt("Carol is gone"u8.ToArray());

            // Assert
            // Bob should be able to decrypt the new message with his updated session.
            var decryptedByBob = bobManager.GroupSession.Decrypt(messageAfterRemoval);
            Encoding.UTF8.GetString(decryptedByBob).Should().Be("Carol is gone");

            // Carol should NOT be able to decrypt the new message with her old session.
            Assert.Throws<CryptographicException>(() => carolOldGroupSession.Decrypt(messageAfterRemoval));
        }
    }
}
