using System.Text;
using FluentAssertions;
using System.Security.Cryptography;
using System.Text.Json;

namespace Percolator.Cryptography.Tests
{
    [TestFixture]
    public class GroupManagerTests
    {
        private byte[] _masterKey = null!;

        // Use distinct keys for creator and members to avoid confusion
        private ECDiffieHellman _creatorIdentity = null!;
        private ECDiffieHellman _aliceIdentity = null!;
        private ECDiffieHellman _bobIdentity = null!;

        // Pre-keys for members
        private ECDiffieHellman _aliceRatchet = null!;
        private ECDiffieHellman _bobRatchet = null!;

        [SetUp]
        public void Setup()
        {
            _masterKey = RandomNumberGenerator.GetBytes(32);
            _creatorIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _aliceIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _bobIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _aliceRatchet = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _bobRatchet = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        }

        [TearDown]
        public void Teardown()
        {
            _creatorIdentity.Dispose();
            _aliceIdentity.Dispose();
            _bobIdentity.Dispose();
            _aliceRatchet.Dispose();
            _bobRatchet.Dispose();
        }

        [Test]
        public void FullGroupLifecycle_ShouldSucceed()
        {
            // 1. Setup: Creator and two new members (Alice, Bob)
            var creatorManager = new GroupManager(_creatorIdentity);
            
            // Establish 1-on-1 sessions for invitations
            var sharedSecretAlice = _creatorIdentity.DeriveKeyMaterial(_aliceIdentity.PublicKey);
            var sessionToAlice = DoubleRatchetSession.AsInitiator(sharedSecretAlice, _creatorIdentity, _aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _aliceRatchet.PublicKey.ExportSubjectPublicKeyInfo());
            var sharedSecretBob = _creatorIdentity.DeriveKeyMaterial(_bobIdentity.PublicKey);
            var sessionToBob = DoubleRatchetSession.AsInitiator(sharedSecretBob, _creatorIdentity, _bobIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _bobRatchet.PublicKey.ExportSubjectPublicKeyInfo());

            // 2. Invitations
            var invitationToAlice = creatorManager.CreateInvitation("alice", sessionToAlice);
            var invitationToBob = creatorManager.CreateInvitation("bob", sessionToBob);

            // 3. Members accept invitations
            var sessionFromAlice = DoubleRatchetSession.AsResponder(sharedSecretAlice, _aliceIdentity, _creatorIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _aliceRatchet);
            var aliceGroupManager = GroupManager.AcceptInvitation(sessionFromAlice, invitationToAlice, creatorManager.SigningPublicKey!, _aliceIdentity);
            
            var sessionFromBob = DoubleRatchetSession.AsResponder(sharedSecretBob, _bobIdentity, _creatorIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _bobRatchet);
            var bobGroupManager = GroupManager.AcceptInvitation(sessionFromBob, invitationToBob, creatorManager.SigningPublicKey!, _bobIdentity);
            
            aliceGroupManager.GroupId.Should().Be(creatorManager.GroupId);
            bobGroupManager.GroupId.Should().Be(creatorManager.GroupId);

            // 4. Communication
            var messageFromCreator = creatorManager.GroupSession.Encrypt("Welcome!"u8.ToArray());
            Encoding.UTF8.GetString(aliceGroupManager.GroupSession.Decrypt(messageFromCreator)).Should().Be("Welcome!");
            Encoding.UTF8.GetString(bobGroupManager.GroupSession.Decrypt(messageFromCreator)).Should().Be("Welcome!");

            // 5. State Persistence
            var savedState = creatorManager.SaveState(_masterKey);
            var loadedCreatorManager = GroupManager.LoadState(savedState, _masterKey, _creatorIdentity);
            var oldGroupId = creatorManager.GroupId;

            // 6. Member Removal and Re-keying
            var rekeyMessages = loadedCreatorManager.RemoveMember("alice");
            rekeyMessages.Should().HaveCount(1);
            rekeyMessages.Should().ContainKey("bob");
            loadedCreatorManager.GroupId.Should().NotBe(oldGroupId); // Group ID must change

            // 7. Bob processes the re-key message
            bobGroupManager.ProcessRekeyMessage(sessionFromBob, rekeyMessages["bob"]);
            bobGroupManager.GroupId.Should().Be(loadedCreatorManager.GroupId); // Bob is in the new group

            // 8. Communication in the new group
            var messageInNewGroup = loadedCreatorManager.GroupSession.Encrypt("Alice is gone"u8.ToArray());
            Encoding.UTF8.GetString(bobGroupManager.GroupSession.Decrypt(messageInNewGroup)).Should().Be("Alice is gone");

            // Alice should not be able to decrypt the new message
            Action act = () => aliceGroupManager.GroupSession.Decrypt(messageInNewGroup);
            act.Should().Throw<CryptographicException>();
            
            // Dispose all managers
            creatorManager.Dispose();
            loadedCreatorManager.Dispose();
            aliceGroupManager.Dispose();
            bobGroupManager.Dispose();
        }

        [Test]
        public void AcceptInvitation_WithTamperedSignature_ThrowsException()
        {
            // Arrange
            var creatorManager = new GroupManager(_creatorIdentity);
            var sharedSecret = _creatorIdentity.DeriveKeyMaterial(_aliceIdentity.PublicKey);
            var sessionToAlice = DoubleRatchetSession.AsInitiator(sharedSecret, _creatorIdentity, _aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _aliceRatchet.PublicKey.ExportSubjectPublicKeyInfo());

            // 1. Create the unsigned part of the message
            var unsignedMessage = new UnsignedGroupControlMessage
            {
                SessionKey = creatorManager.GroupSession.SessionKey,
                GroupId = creatorManager.GroupId
            };
            var unsignedPayload = JsonSerializer.SerializeToUtf8Bytes(unsignedMessage);

            // 2. Create a signed message with a bad signature
            var signedMessage = new SignedGroupControlMessage
            {
                UnsignedMessage = unsignedPayload,
                Signature = RandomNumberGenerator.GetBytes(64) // Bad signature
            };
            var tamperedPayload = JsonSerializer.SerializeToUtf8Bytes(signedMessage);

            // 3. Encrypt for delivery
            var tamperedInvitation = sessionToAlice.Encrypt(tamperedPayload);

            // Act & Assert
            var sessionFromAlice = DoubleRatchetSession.AsResponder(sharedSecret, _aliceIdentity, _creatorIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _aliceRatchet);
            Action act = () => GroupManager.AcceptInvitation(sessionFromAlice, tamperedInvitation, creatorManager.SigningPublicKey!, _aliceIdentity);

            act.Should().Throw<CryptographicException>().WithMessage("Invalid signature on invitation.");

            creatorManager.Dispose();
        }
        
        [Test]
        public void NonCreator_CannotPerformAdminActions()
        {
            // Arrange: Create a group and have Alice join
            var creatorManager = new GroupManager(_creatorIdentity);
            var sharedSecret = _creatorIdentity.DeriveKeyMaterial(_aliceIdentity.PublicKey);
            var sessionToAlice = DoubleRatchetSession.AsInitiator(sharedSecret, _creatorIdentity, _aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _aliceRatchet.PublicKey.ExportSubjectPublicKeyInfo());
            var invitation = creatorManager.CreateInvitation("alice", sessionToAlice);
            var sessionFromAlice = DoubleRatchetSession.AsResponder(sharedSecret, _aliceIdentity, _creatorIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _aliceRatchet);
            var aliceGroupManager = GroupManager.AcceptInvitation(sessionFromAlice, invitation, creatorManager.SigningPublicKey!, _aliceIdentity);

            // Act & Assert: Alice tries to invite Bob
            var sharedSecretBob = _aliceIdentity.DeriveKeyMaterial(_bobIdentity.PublicKey);
            var sessionToBobForAlice = DoubleRatchetSession.AsInitiator(sharedSecretBob, _aliceIdentity, _bobIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _bobRatchet.PublicKey.ExportSubjectPublicKeyInfo());
            Action inviteAction = () => aliceGroupManager.CreateInvitation("bob", sessionToBobForAlice);
            inviteAction.Should().Throw<InvalidOperationException>().WithMessage("Only the group creator can send invitations.");
            
            // Act & Assert: Alice tries to remove herself (or anyone)
            Action removeAction = () => aliceGroupManager.RemoveMember("alice");
            removeAction.Should().Throw<InvalidOperationException>().WithMessage("Only the group creator can remove members.");
            
            // Act & Assert: Alice tries to save state
            Action saveAction = () => aliceGroupManager.SaveState(_masterKey);
            saveAction.Should().Throw<InvalidOperationException>().WithMessage("Only the group creator can save state.");
            
            creatorManager.Dispose();
            aliceGroupManager.Dispose();
            sessionToBobForAlice.Dispose();
        }

        [Test]
        public void ProcessRekeyMessage_WithMismatchedGroupId_ThrowsException()
        {
            // Arrange: Create two separate groups with the same creator and a common member (Bob).
            var groupAManager = new GroupManager(_creatorIdentity);
            var groupBManager = new GroupManager(_creatorIdentity);

            // Bob joins Group A
            var sharedSecretA = _creatorIdentity.DeriveKeyMaterial(_bobIdentity.PublicKey);
            var sessionToBobForA = DoubleRatchetSession.AsInitiator(sharedSecretA, _creatorIdentity, _bobIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _bobRatchet.PublicKey.ExportSubjectPublicKeyInfo());
            var invitationA = groupAManager.CreateInvitation("bob", sessionToBobForA);
            using var sessionFromBobForA = DoubleRatchetSession.AsResponder(sharedSecretA, _bobIdentity, _creatorIdentity.PublicKey.ExportSubjectPublicKeyInfo(), _bobRatchet);
            var bobManagerForA = GroupManager.AcceptInvitation(sessionFromBobForA, invitationA, groupAManager.SigningPublicKey!, _bobIdentity);

            // Bob joins Group B
            using var bobRatchetB = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var sharedSecretB = _creatorIdentity.DeriveKeyMaterial(_bobIdentity.PublicKey);
            var sessionToBobForB = DoubleRatchetSession.AsInitiator(sharedSecretB, _creatorIdentity, _bobIdentity.PublicKey.ExportSubjectPublicKeyInfo(), bobRatchetB.PublicKey.ExportSubjectPublicKeyInfo());
            var invitationB = groupBManager.CreateInvitation("bob", sessionToBobForB);
            using var sessionFromBobForB = DoubleRatchetSession.AsResponder(sharedSecretB, _bobIdentity, _creatorIdentity.PublicKey.ExportSubjectPublicKeyInfo(), bobRatchetB);
            var bobManagerForB = GroupManager.AcceptInvitation(sessionFromBobForB, invitationB, groupBManager.SigningPublicKey!, _bobIdentity);

            // Add a dummy member to Group A, so we can remove them to trigger a re-key.
            using var dummyIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var dummyRatchet = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var sharedSecretDummy = _creatorIdentity.DeriveKeyMaterial(dummyIdentity.PublicKey);
            var sessionToDummy = DoubleRatchetSession.AsInitiator(sharedSecretDummy, _creatorIdentity, dummyIdentity.PublicKey.ExportSubjectPublicKeyInfo(), dummyRatchet.PublicKey.ExportSubjectPublicKeyInfo());
            groupAManager.CreateInvitation("dummy_member", sessionToDummy);

            // Act: Creator re-keys Group A by removing the dummy member.
            var rekeyMessagesForA = groupAManager.RemoveMember("dummy_member");
            var rekeyMessageForBobInA = rekeyMessagesForA["bob"];

            // Assert: Bob's manager for Group B must reject the re-key message from Group A,
            // even when using the correct session (from Group A) to decrypt it.
            // This verifies the OldGroupId check.
            Action act = () => bobManagerForB.ProcessRekeyMessage(sessionFromBobForA, rekeyMessageForBobInA);
            act.Should().Throw<CryptographicException>().WithMessage("Invalid signature on re-key message.");

            // Cleanup
            groupAManager.Dispose();
            groupBManager.Dispose();
            bobManagerForA.Dispose();
            bobManagerForB.Dispose();
        }

        [Test]
        public void AcceptInvitation_WithTamperedUnsignedMessage_ThrowsException()
        {
            // Arrange
            var creatorManager = new GroupManager(_creatorIdentity);
            var sessionToAlice = DoubleRatchetSession.AsInitiator(
                _creatorIdentity.DeriveKeyMaterial(_aliceIdentity.PublicKey),
                _creatorIdentity,
                _aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo(),
                _aliceRatchet.PublicKey.ExportSubjectPublicKeyInfo());

            // 1. Create a valid invitation to get a valid signature
            var validInvitation = creatorManager.CreateInvitation("alice", sessionToAlice);
            var sessionFromAlice = DoubleRatchetSession.AsResponder(
                _creatorIdentity.DeriveKeyMaterial(_aliceIdentity.PublicKey),
                _aliceIdentity,
                _creatorIdentity.PublicKey.ExportSubjectPublicKeyInfo(),
                _aliceRatchet);
            var validPayloadBytes = sessionFromAlice.Decrypt(validInvitation);
            var validSignedMessage = JsonSerializer.Deserialize<SignedGroupControlMessage>(validPayloadBytes)!;

            // 2. Create a tampered unsigned message
            var tamperedUnsignedMessage = new UnsignedGroupControlMessage
            {
                GroupId = Guid.NewGuid().ToString(), // Different Group ID
                SessionKey = RandomNumberGenerator.GetBytes(32) // Different Session Key
            };
            var tamperedUnsignedBytes = JsonSerializer.SerializeToUtf8Bytes(tamperedUnsignedMessage);

            // 3. Create a malicious signed message using the *valid* signature but the *tampered* content
            var maliciousSignedMessage = new SignedGroupControlMessage
            {
                UnsignedMessage = tamperedUnsignedBytes,
                Signature = validSignedMessage.Signature // Use the original, valid signature
            };
            var maliciousPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(maliciousSignedMessage);

            // 4. Encrypt the malicious payload for delivery
            var tamperedInvitation = sessionToAlice.Encrypt(maliciousPayloadBytes);

            // Act & Assert
            Action act = () => GroupManager.AcceptInvitation(sessionFromAlice, tamperedInvitation, creatorManager.SigningPublicKey!, _aliceIdentity);
            act.Should().Throw<CryptographicException>().WithMessage("Invalid signature on invitation.");

            // Cleanup
            creatorManager.Dispose();
            sessionToAlice.Dispose();
            sessionFromAlice.Dispose();
        }
    }
}
