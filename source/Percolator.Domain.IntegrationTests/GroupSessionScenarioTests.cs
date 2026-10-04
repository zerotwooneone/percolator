using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.IntegrationTests.Builders;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests;

[TestFixture]
public sealed class GroupSessionScenarioTests
{
    private ScenarioCryptoEngine _cryptoEngine = null!;
    private ScenarioZkProofEngine _zkProofEngine = null!;
    private InMemoryGroupCredentialsRepository _credentialsRepo = null!;
    private FixedTimeProvider _timeProvider = null!;

    private sealed class FixedTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 3, 14, 0, 0, TimeSpan.Zero);
    }

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new ScenarioCryptoEngine();
        _zkProofEngine = new ScenarioZkProofEngine();
        _credentialsRepo = new InMemoryGroupCredentialsRepository();
        _timeProvider = new FixedTimeProvider();
    }

    [Test]
    public async Task GroupSession_SenderKeys_AuthorSignatures_ZkRelayGating_And_MembershipMutation()
    {
        // -------------------------------------------------------------------------
        // 1. SETUP GROUP VIA BUILDER: Alice (Admin), Bob, and Charlie
        // -------------------------------------------------------------------------
        using var alice = ScenarioParticipant.Create("Alice", _cryptoEngine);
        using var bob = ScenarioParticipant.Create("Bob", _cryptoEngine);
        using var charlie = ScenarioParticipant.Create("Charlie", _cryptoEngine);

        var context = await ScenarioGroupBuilder
            .Create("Operations Room", alice, _cryptoEngine, _zkProofEngine, _timeProvider)
            .WithMember(bob)
            .WithMember(charlie)
            .BuildAsync(_credentialsRepo);

        using (context)
        {
            var channel = context.Channel;
            var ledger = context.Ledger;
            var credentials = context.Credentials;

            channel.Members.Should().HaveCount(3);

            // -------------------------------------------------------------------------
            // 2. RELAY GROUP LEDGER & ZERO-KNOWLEDGE GATED DISPATCH
            // -------------------------------------------------------------------------
            var wirePayload = Encoding.UTF8.GetBytes("Encrypted wire envelope payload for group");
            Span<byte> challengeHash = stackalloc byte[32];
            SHA256.HashData(wirePayload, challengeHash);

            // Alice generates valid ZK presentation proof of membership
            var zkProof = _zkProofEngine.GenerateGroupPresentation(
                ledger.CurrentEpoch.Value,
                challengeHash,
                credentials.MasterKey.Span,
                credentials.AuthCredentialMac.Span);

            // Relay verifies membership before dispatching
            var dispatchResult = ledger.VerifyDispatch(zkProof, wirePayload, _zkProofEngine);
            dispatchResult.IsSuccess.Should().BeTrue("Valid ZK presentation proof must authorize relay dispatch.");

            // Forgery check: Attacker with tampered proof is rejected
            var forgedProof = ZkPresentationBytes.FromBytesOwned(new byte[68]);
            var forgedDispatch = ledger.VerifyDispatch(forgedProof, wirePayload, _zkProofEngine);
            forgedDispatch.IsFailure.Should().BeTrue();
            forgedDispatch.Error.Code.Should().Be("INVALID_ZK_PROOF");

            // -------------------------------------------------------------------------
            // 3. BROADCAST TRANSMISSION, SIGNING & AEAD DECRYPTION (5 Messages)
            // -------------------------------------------------------------------------
            const int messageCount = 5;
            var groupPackets = new List<WirePacket>();

            var aliceSender = context.GetSender(alice);
            var bobReceiver = context.GetReceiver(bob, alice);
            var charlieReceiver = context.GetReceiver(charlie, alice);

            for (int i = 0; i < messageCount; i++)
            {
                var text = $"Group broadcast message #{i} from Admin Alice";
                var packet = WirePacketSimulator.PackGroup(aliceSender, text, _cryptoEngine);
                groupPackets.Add(packet);
            }

            // Bob receives and verifies messages sequentially
            for (int i = 0; i < messageCount; i++)
            {
                var packet = groupPackets[i];
                var decrypted = WirePacketSimulator.UnpackGroup(bobReceiver, packet, _cryptoEngine);
                decrypted.Should().Be(packet.Plaintext);
            }

            // -------------------------------------------------------------------------
            // 4. TAMPER AND IMPERSONATION DEFENSE
            // -------------------------------------------------------------------------
            // Tamper test: Corrupted signature fails author signature verification
            var corruptSignature = (byte[])groupPackets[0].Signature!.Clone();
            corruptSignature[0] ^= 0xFF;
            var corruptSigVerify = bobReceiver.VerifyAuthorSignature(
                Encoding.UTF8.GetBytes(groupPackets[0].Plaintext!),
                corruptSignature,
                _cryptoEngine);
            corruptSigVerify.IsFailure.Should().BeTrue();
            corruptSigVerify.Error.Code.Should().Be("INVALID_SIGNATURE");

            // Impersonation test: Charlie attempts to forge a message under Alice's sender key
            var forgedPayload = Encoding.UTF8.GetBytes("Forged message pretending to be Alice");
            var forgedSignature = _cryptoEngine.SignEd25519(charlie.IdentityPrivateKey.Span, forgedPayload);
            var forgedVerify = bobReceiver.VerifyAuthorSignature(forgedPayload, forgedSignature, _cryptoEngine);
            forgedVerify.IsFailure.Should().BeTrue("Bob must reject signatures not matching Alice's registered author signing key.");

            // -------------------------------------------------------------------------
            // 5. OUT-OF-ORDER GROUP DELIVERY (Charlie receives: 4, 1, 0, 3, 2)
            // -------------------------------------------------------------------------
            var shuffle = new[] { 4, 1, 0, 3, 2 };
            foreach (var idx in shuffle)
            {
                var packet = groupPackets[idx];
                var decrypted = WirePacketSimulator.UnpackGroup(charlieReceiver, packet, _cryptoEngine);
                decrypted.Should().Be(packet.Plaintext);
            }

            // -------------------------------------------------------------------------
            // 6. MEMBERSHIP MUTATION & SENDER KEY ROTATION (Charlie Removed)
            // -------------------------------------------------------------------------
            var removeCharlie = channel.RemoveMember(alice.IdentityId, charlie.IdentityId, _timeProvider);
            removeCharlie.IsSuccess.Should().BeTrue();
            channel.Members.Should().NotContain(m => m.Id == charlie.IdentityId);

            // Alice rotates to KeyId: 2 with fresh sender keys distributed only to active members
            var (aliceSenderEpoch2, newReceivers) = context.RotateSenderKey(
                alice,
                newKeyId: 2,
                activeMembers: new[] { alice, bob });

            var bobReceiverEpoch2 = newReceivers.Single(r => r.AuthorId == alice.IdentityId);

            // Alice broadcasts post-revocation message under KeyId: 2
            const string postRevocationMsg = "Charlie has been removed. KeyId: 2 active.";
            var postRevocationPacket = WirePacketSimulator.PackGroup(aliceSenderEpoch2, postRevocationMsg, _cryptoEngine);

            // Bob decrypts KeyId: 2 successfully
            var decryptedBob2 = WirePacketSimulator.UnpackGroup(bobReceiverEpoch2, postRevocationPacket, _cryptoEngine);
            decryptedBob2.Should().Be(postRevocationMsg);

            // Charlie (having only KeyId: 1 receiver) cannot decrypt KeyId: 2 traffic
            charlieReceiver.KeyId.Should().Be(1);
            var charlieAttempt = charlieReceiver.TryAdvanceToIteration(5, _cryptoEngine);
            charlieAttempt.IsSuccess.Should().BeTrue();
            using (charlieAttempt.Value)
            {
                var charlieDecryptAction = () => _cryptoEngine.DecryptAesGcm(
                    charlieAttempt.Value.Span,
                    postRevocationPacket.Nonce,
                    postRevocationPacket.Ciphertext,
                    postRevocationPacket.AssociatedData!);

                charlieDecryptAction.Should().Throw<CryptographicException>(
                    "Charlie lacks KeyId: 2 and cannot decrypt post-revocation traffic.");
            }
        }
    }
}
