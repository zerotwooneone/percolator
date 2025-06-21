using FluentAssertions;
using NUnit.Framework;
using Pecolator.Cryptography;
using System.Security.Cryptography;
using System.Text;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class GroupManagerTests
    {
        private ECDiffieHellman _aliceIdentityKey = null!;
        private ECDiffieHellman _bobIdentityKey = null!;
        private ECDiffieHellman _bobPreKey = null!;

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
        public void CreateAndAcceptInvitation_EstablishesSharedGroupSession()
        {
            // Arrange: Establish a 1-on-1 session between Alice and Bob first.
            var aliceManager = new GroupManager();
            var bobManager = new GroupManager();

            var sharedSecret = _aliceIdentityKey.DeriveKeyMaterial(_bobPreKey.PublicKey);
            using var aliceToBobSession = new DoubleRatchetSession(sharedSecret, _aliceIdentityKey, SessionRole.Initiator, _bobPreKey.PublicKey.ExportSubjectPublicKeyInfo());
            using var bobToAliceSession = new DoubleRatchetSession(sharedSecret, _bobIdentityKey, SessionRole.Responder, ownInitialRatchetKey: _bobPreKey);

            // Act: Alice creates a group and invites Bob.
            var (invitation, aliceGroupSession) = aliceManager.CreateGroupAndInvitation(aliceToBobSession);
            var bobGroupSession = bobManager.AcceptInvitation(bobToAliceSession, invitation);

            // Assert: Both should have a valid session for the same group.
            bobGroupSession.Should().NotBeNull();
            var groupId = Encoding.UTF8.GetString(aliceGroupSession.Context);
            var bobGroupId = Encoding.UTF8.GetString(bobGroupSession!.Context);
            groupId.Should().Be(bobGroupId);

            // Assert: They can communicate in the group.
            var plaintext = "Welcome to the group!";
            var message = aliceGroupSession.Encrypt(Encoding.UTF8.GetBytes(plaintext));
            var decryptedBytes = bobGroupSession.Decrypt(message);

            Encoding.UTF8.GetString(decryptedBytes).Should().Be(plaintext);
        }
    }
}
