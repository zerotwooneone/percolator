using NUnit.Framework;
using System;
using System.Linq;
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
            var creatorManager = new GroupManager();
            
            // Establish 1-on-1 sessions for invitations
            var sessionToAlice = DoubleRatchetSession.CreateInitiatorSession(_creatorIdentity, _aliceIdentity, _aliceRatchet);
            var sessionToBob = DoubleRatchetSession.CreateInitiatorSession(_creatorIdentity, _bobIdentity, _bobRatchet);

            // 2. Invitations
            var invitationToAlice = creatorManager.CreateInvitation("alice", sessionToAlice);
            var invitationToBob = creatorManager.CreateInvitation("bob", sessionToBob);

            // 3. Members accept invitations
            var sessionFromAlice = DoubleRatchetSession.CreateResponderSession(_aliceIdentity, _aliceRatchet, _creatorIdentity);
            var aliceGroupManager = GroupManager.AcceptInvitation(sessionFromAlice, invitationToAlice, creatorManager.SigningPublicKey!);
            
            var sessionFromBob = DoubleRatchetSession.CreateResponderSession(_bobIdentity, _bobRatchet, _creatorIdentity);
            var bobGroupManager = GroupManager.AcceptInvitation(sessionFromBob, invitationToBob, creatorManager.SigningPublicKey!);
            
            aliceGroupManager.GroupId.Should().Be(creatorManager.GroupId);
            bobGroupManager.GroupId.Should().Be(creatorManager.GroupId);

            // 4. Communication
            var messageFromCreator = creatorManager.GroupSession.Encrypt("Welcome!"u8.ToArray());
            Encoding.UTF8.GetString(aliceGroupManager.GroupSession.Decrypt(messageFromCreator)).Should().Be("Welcome!");
            Encoding.UTF8.GetString(bobGroupManager.GroupSession.Decrypt(messageFromCreator)).Should().Be("Welcome!");

            // 5. State Persistence
            var savedState = creatorManager.SaveState(_masterKey);
            var loadedCreatorManager = GroupManager.LoadState(savedState, _masterKey);
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
            var creatorManager = new GroupManager();
            var sessionToAlice = DoubleRatchetSession.CreateInitiatorSession(_creatorIdentity, _aliceIdentity, _aliceRatchet);

            // Manually create a control message with a bad signature
            var controlMessage = new GroupControlMessage
            {
                SessionKey = creatorManager.GroupSession.SessionKey,
                GroupId = creatorManager.GroupId,
                Signature = RandomNumberGenerator.GetBytes(64) // Bad signature
            };
            var tamperedPayload = JsonSerializer.SerializeToUtf8Bytes(controlMessage);
            var tamperedInvitation = sessionToAlice.Encrypt(tamperedPayload);

            // Act & Assert
            var sessionFromAlice = DoubleRatchetSession.CreateResponderSession(_aliceIdentity, _aliceRatchet, _creatorIdentity);
            Action act = () => GroupManager.AcceptInvitation(sessionFromAlice, tamperedInvitation, creatorManager.SigningPublicKey!);
            
            act.Should().Throw<CryptographicException>().WithMessage("Invalid signature on invitation.");
            
            creatorManager.Dispose();
        }
        
        [Test]
        public void NonCreator_CannotPerformAdminActions()
        {
            // Arrange: Create a group and have Alice join
            var creatorManager = new GroupManager();
            var sessionToAlice = DoubleRatchetSession.CreateInitiatorSession(_creatorIdentity, _aliceIdentity, _aliceRatchet);
            var invitation = creatorManager.CreateInvitation("alice", sessionToAlice);
            var sessionFromAlice = DoubleRatchetSession.CreateResponderSession(_aliceIdentity, _aliceRatchet, _creatorIdentity);
            var aliceGroupManager = GroupManager.AcceptInvitation(sessionFromAlice, invitation, creatorManager.SigningPublicKey!);

            // Act & Assert: Alice tries to invite Bob
            var sessionToBobForAlice = DoubleRatchetSession.CreateInitiatorSession(_aliceIdentity, _bobIdentity, _bobRatchet);
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
    }
}
