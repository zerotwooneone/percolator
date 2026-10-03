using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests;

[TestFixture]
public sealed class GroupSessionScenarioTests
{
    private ScenarioCryptoEngine _cryptoEngine = null!;
    private ScenarioZkProofEngine _zkProofEngine = null!;
    private IGroupCredentialsRepository _credentialsRepo = null!;
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
        // 1. CHANNEL GENESIS & MEMBERSHIP
        // -------------------------------------------------------------------------
        var channelId = ChannelId.New();
        var aliceId = PublicIdentityId.New();
        var bobId = PublicIdentityId.New();
        var charlieId = PublicIdentityId.New();
        var relayId = PublicIdentityId.New();

        var aliceDeviceId = DeviceId.Primary;
        var bobDeviceId = DeviceId.Primary;
        var charlieDeviceId = DeviceId.Primary;

        var channelResult = GroupChannel.CreateGenesis(channelId, aliceId, "Operations Room", _timeProvider);
        channelResult.IsSuccess.Should().BeTrue();
        var channel = channelResult.Value;

        channel.AddMember(aliceId, bobId, ChannelRole.Member, _timeProvider).IsSuccess.Should().BeTrue();
        channel.AddMember(aliceId, charlieId, ChannelRole.Member, _timeProvider).IsSuccess.Should().BeTrue();
        channel.Members.Should().HaveCount(3);

        // -------------------------------------------------------------------------
        // 2. CLIENT-SIDE GROUP CREDENTIALS & AGGREGATE PERSISTENCE
        // -------------------------------------------------------------------------
        var masterKeyBytes = new byte[32];
        RandomNumberGenerator.Fill(masterKeyBytes);
        using var groupMasterKey = GroupMasterKey.FromSpan(masterKeyBytes);

        var authMacBytes = new byte[32];
        RandomNumberGenerator.Fill(authMacBytes);
        var authMac = AuthCredentialMacBytes.FromSpan(authMacBytes);

        var credsResult = GroupCredentials.CreateGenesis(channelId, groupMasterKey, authMac);
        credsResult.IsSuccess.Should().BeTrue();
        using var credentials = credsResult.Value;

        await _credentialsRepo.SaveAsync(credentials);
        var storedCreds = await _credentialsRepo.GetAsync(channelId);
        storedCreds.Should().NotBeNull();
        storedCreds!.Id.Should().Be(channelId);

        // -------------------------------------------------------------------------
        // 3. RELAY GROUP LEDGER & ZERO-KNOWLEDGE GATED DISPATCH
        // -------------------------------------------------------------------------
        var aliceToken = BlindedRoutingToken.New();
        var bobToken = BlindedRoutingToken.New();
        var charlieToken = BlindedRoutingToken.New();

        var publicParams = ZkGroupPublicParams.FromBytesOwned(new byte[64]);
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[128]);

        var ledgerResult = RelayGroupLedger.CreateGenesis(
            channelId,
            relayId,
            genesisBlob,
            new HashSet<BlindedRoutingToken> { aliceToken, bobToken, charlieToken },
            publicParams,
            _timeProvider);
        ledgerResult.IsSuccess.Should().BeTrue();
        var ledger = ledgerResult.Value;

        // Alice prepares a group wire envelope
        var wirePayload = Encoding.UTF8.GetBytes("Encrypted wire envelope payload for group");
        Span<byte> challengeHash = stackalloc byte[32];
        SHA256.HashData(wirePayload, challengeHash);

        // Alice generates a ZK presentation proof of membership
        var zkProof = _zkProofEngine.GenerateGroupPresentation(
            ledger.CurrentEpoch.Value,
            challengeHash,
            credentials.MasterKey.Span,
            credentials.AuthCredentialMac.Span);

        // Relay verifies membership before dispatching
        var dispatchResult = ledger.VerifyDispatch(zkProof, wirePayload, _zkProofEngine);
        dispatchResult.IsSuccess.Should().BeTrue("Valid ZK presentation proof must authorize relay dispatch.");

        // Forgery check: Eve (an attacker) with wrong epoch or tampered proof is rejected
        var forgedProof = ZkPresentationBytes.FromBytesOwned(new byte[68]); // All zeros
        var forgedDispatch = ledger.VerifyDispatch(forgedProof, wirePayload, _zkProofEngine);
        forgedDispatch.IsFailure.Should().BeTrue();
        forgedDispatch.Error.Code.Should().Be("INVALID_ZK_PROOF");

        // -------------------------------------------------------------------------
        // 4. SENDER KEYS SETUP (Alice publishes with KeyId: 1)
        // -------------------------------------------------------------------------
        var (aliceSignPriv, aliceSignPub) = _cryptoEngine.GenerateIdentityKeyPair();
        var aliceInitialChainKey = ChainKey.FromSpan(SHA256.HashData(credentials.MasterKey.Span));

        using var aliceRatchet = new GroupSenderKeyRatchet(
            channelId,
            aliceId,
            aliceDeviceId,
            aliceInitialChainKey,
            initialIteration: 0,
            keyId: 1,
            signingPrivateKey: aliceSignPriv,
            authorSigningPublicKey: aliceSignPub);

        // Bob and Charlie receive Alice's sender key distribution
        using var bobReceiver = new GroupReceiverSession(
            channelId,
            aliceId,
            aliceDeviceId,
            aliceInitialChainKey,
            initialIteration: 0,
            keyId: 1,
            authorSigningKey: aliceSignPub);

        using var charlieReceiver = new GroupReceiverSession(
            channelId,
            aliceId,
            aliceDeviceId,
            aliceInitialChainKey,
            initialIteration: 0,
            keyId: 1,
            authorSigningKey: aliceSignPub);

        // -------------------------------------------------------------------------
        // 5. BROADCAST TRANSMISSION, SIGNING & AEAD DECRYPTION (5 Messages)
        // -------------------------------------------------------------------------
        const int messageCount = 5;
        var groupMessages = new List<(uint Iteration, byte[] Ciphertext, byte[] Nonce, byte[] Signature, string Text)>();

        for (int i = 0; i < messageCount; i++)
        {
            var text = $"Group broadcast message #{i} from Admin Alice";
            var textBytes = Encoding.UTF8.GetBytes(text);

            var advance = aliceRatchet.Advance(_cryptoEngine);
            advance.IsSuccess.Should().BeTrue();
            var (iteration, messageKey) = advance.Value;

            var signResult = aliceRatchet.SignPayload(textBytes, _cryptoEngine);
            signResult.IsSuccess.Should().BeTrue();
            var signature = signResult.Value;

            var nonce = new byte[12];
            BitConverter.GetBytes((ulong)iteration).CopyTo(nonce, 0);

            var ciphertext = _cryptoEngine.EncryptAesGcm(
                messageKey.Span,
                nonce,
                textBytes,
                associatedData: Encoding.UTF8.GetBytes($"group-{channelId}-{iteration}"));

            groupMessages.Add((iteration, ciphertext, nonce, signature, text));
        }

        // Bob receives messages sequentially
        for (int i = 0; i < messageCount; i++)
        {
            var msg = groupMessages[i];

            var advanceRecv = bobReceiver.TryAdvanceToIteration(msg.Iteration, _cryptoEngine);
            advanceRecv.IsSuccess.Should().BeTrue();
            using var msgKey = advanceRecv.Value;

            var decryptedBytes = _cryptoEngine.DecryptAesGcm(
                msgKey.Span,
                msg.Nonce,
                msg.Ciphertext,
                associatedData: Encoding.UTF8.GetBytes($"group-{channelId}-{msg.Iteration}"));

            // Verify author signature
            var sigVerify = bobReceiver.VerifyAuthorSignature(decryptedBytes, msg.Signature, _cryptoEngine);
            sigVerify.IsSuccess.Should().BeTrue("Alice's author signature must verify cleanly.");

            Encoding.UTF8.GetString(decryptedBytes).Should().Be(msg.Text);
        }

        // -------------------------------------------------------------------------
        // 6. TAMPER AND IMPERSONATION DEFENSE
        // -------------------------------------------------------------------------
        // Attacker tampers with signature
        var corruptSignature = (byte[])groupMessages[0].Signature.Clone();
        corruptSignature[0] ^= 0xFF;
        var corruptSigVerify = bobReceiver.VerifyAuthorSignature(
            Encoding.UTF8.GetBytes(groupMessages[0].Text),
            corruptSignature,
            _cryptoEngine);
        corruptSigVerify.IsFailure.Should().BeTrue();
        corruptSigVerify.Error.Code.Should().Be("INVALID_SIGNATURE");

        // Attacker tampers with author identity: Charlie attempts to sign a message claiming it was Alice's
        var (charlieSignPriv, _) = _cryptoEngine.GenerateIdentityKeyPair();
        var forgedAlicePayload = Encoding.UTF8.GetBytes("Forged message pretending to be Alice");
        var forgedSignature = _cryptoEngine.SignEd25519(charlieSignPriv.Span, forgedAlicePayload);
        var forgedVerify = bobReceiver.VerifyAuthorSignature(forgedAlicePayload, forgedSignature, _cryptoEngine);
        forgedVerify.IsFailure.Should().BeTrue("Bob must reject signatures not matching Alice's registered author signing key.");

        // -------------------------------------------------------------------------
        // 7. OUT-OF-ORDER GROUP DELIVERY (Charlie receives: 4, 1, 0, 3, 2)
        // -------------------------------------------------------------------------
        var shuffle = new[] { 4, 1, 0, 3, 2 };
        foreach (var idx in shuffle)
        {
            var msg = groupMessages[idx];

            var advanceRecv = charlieReceiver.TryAdvanceToIteration(msg.Iteration, _cryptoEngine);
            advanceRecv.IsSuccess.Should().BeTrue();
            using var msgKey = advanceRecv.Value;

            var decrypted = _cryptoEngine.DecryptAesGcm(
                msgKey.Span,
                msg.Nonce,
                msg.Ciphertext,
                associatedData: Encoding.UTF8.GetBytes($"group-{channelId}-{msg.Iteration}"));

            charlieReceiver.VerifyAuthorSignature(decrypted, msg.Signature, _cryptoEngine).IsSuccess.Should().BeTrue();
            Encoding.UTF8.GetString(decrypted).Should().Be(msg.Text);
        }

        // -------------------------------------------------------------------------
        // 8. MEMBERSHIP MUTATION & SENDER KEY ROTATION (Charlie Removed)
        // -------------------------------------------------------------------------
        var removeCharlie = channel.RemoveMember(aliceId, charlieId, _timeProvider);
        removeCharlie.IsSuccess.Should().BeTrue();
        channel.Members.Should().NotContain(m => m.Id == charlieId);

        // Alice rotates to KeyId: 2 with a fresh master key
        var newMasterBytes = new byte[32];
        RandomNumberGenerator.Fill(newMasterBytes);
        using var newMasterKey = GroupMasterKey.FromSpan(newMasterBytes);

        var newInitialChainKey = ChainKey.FromSpan(SHA256.HashData(newMasterKey.Span));

        using var aliceRatchetEpoch2 = new GroupSenderKeyRatchet(
            channelId,
            aliceId,
            aliceDeviceId,
            newInitialChainKey,
            initialIteration: 0,
            keyId: 2,
            signingPrivateKey: aliceSignPriv,
            authorSigningPublicKey: aliceSignPub);

        // Bob is updated with KeyId: 2
        using var bobReceiverEpoch2 = new GroupReceiverSession(
            channelId,
            aliceId,
            aliceDeviceId,
            newInitialChainKey,
            initialIteration: 0,
            keyId: 2,
            authorSigningKey: aliceSignPub);

        // Alice sends post-revocation message under KeyId: 2
        var postRevocationMsg = "Charlie has been removed. KeyId: 2 active.";
        var advEpoch2 = aliceRatchetEpoch2.Advance(_cryptoEngine);
        advEpoch2.IsSuccess.Should().BeTrue();

        var nonceEpoch2 = new byte[12];
        var cipherEpoch2 = _cryptoEngine.EncryptAesGcm(
            advEpoch2.Value.Key.Span,
            nonceEpoch2,
            Encoding.UTF8.GetBytes(postRevocationMsg),
            associatedData: Encoding.UTF8.GetBytes($"group-{channelId}-keyid-2"));

        // Bob decrypts KeyId: 2 successfully
        var bobRecv2 = bobReceiverEpoch2.TryAdvanceToIteration(0, _cryptoEngine);
        bobRecv2.IsSuccess.Should().BeTrue();
        using (bobRecv2.Value)
        {
            var decryptedBob2 = _cryptoEngine.DecryptAesGcm(
                bobRecv2.Value.Span,
                nonceEpoch2,
                cipherEpoch2,
                associatedData: Encoding.UTF8.GetBytes($"group-{channelId}-keyid-2"));
            Encoding.UTF8.GetString(decryptedBob2).Should().Be(postRevocationMsg);
        }

        // Charlie (having only KeyId: 1 receiver) cannot decrypt KeyId: 2
        charlieReceiver.KeyId.Should().Be(1);
        var charlieAttempt = charlieReceiver.TryAdvanceToIteration(5, _cryptoEngine);
        charlieAttempt.IsSuccess.Should().BeTrue();
        using (charlieAttempt.Value)
        {
            var charlieDecryptAction = () => _cryptoEngine.DecryptAesGcm(
                charlieAttempt.Value.Span,
                nonceEpoch2,
                cipherEpoch2,
                associatedData: Encoding.UTF8.GetBytes($"group-{channelId}-keyid-2"));

            charlieDecryptAction.Should().Throw<CryptographicException>(
                "Charlie lacks KeyId: 2 and cannot decrypt post-revocation traffic.");
        }
    }
}
