using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.IntegrationTests.Builders;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests;

[TestFixture]
public sealed class DirectSessionScenarioTests
{
    private ScenarioCryptoEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new ScenarioCryptoEngine();
    }

    [Test]
    public void DirectSession_RealWorld_FullLifecycle_BidirectionalRatcheting_And_OutOfOrderDelivery()
    {
        // -------------------------------------------------------------------------
        // 1. SETUP IDENTITIES & ESTABLISH MUTUAL SESSIONS VIA BUILDER
        // -------------------------------------------------------------------------
        using var alice = ScenarioParticipant.Create("Alice", _engine);
        using var bob = ScenarioParticipant.Create("Bob", _engine);

        var (aliceSession, bobSession) = ScenarioSessionPairBuilder
            .Between(alice, bob, _engine)
            .ViaDirectX3dh();

        using (aliceSession)
        using (bobSession)
        {
            // -------------------------------------------------------------------------
            // 2. BURST MESSAGING WITH REAL AES-256-GCM AEAD ENCRYPTION
            // -------------------------------------------------------------------------
            const int burstCount = 5;
            var sentPackets = new List<WirePacket>();

            for (int i = 0; i < burstCount; i++)
            {
                var plaintext = $"Real-world confidential message {i} from Alice to Bob";
                var packet = WirePacketSimulator.PackDirect(aliceSession, plaintext, _engine);
                sentPackets.Add(packet);
            }

            aliceSession.SendingCounter.Should().Be(burstCount);

            // -------------------------------------------------------------------------
            // 3. OUT-OF-ORDER RECEIPT & SKIPPED-KEY RETRIEVAL
            // -------------------------------------------------------------------------
            // Packets arrive in shuffled order: 4, 1, 3, 0, 2
            var arrivalSequence = new[] { 4, 1, 3, 0, 2 };

            foreach (var index in arrivalSequence)
            {
                var packet = sentPackets[index];
                var decryptedText = WirePacketSimulator.UnpackDirect(bobSession, packet, _engine);
                decryptedText.Should().Be(packet.Plaintext);
            }

            bobSession.ReceivingCounter.Should().Be(burstCount);

            // -------------------------------------------------------------------------
            // 4. ANTI-REPLAY DEFENSE
            // -------------------------------------------------------------------------
            // Re-attempting to retrieve already consumed skipped key must fail
            var replayAttempt = bobSession.TryGetSkippedKey(1);
            replayAttempt.IsFailure.Should().BeTrue();
            replayAttempt.Error.Code.Should().Be("SKIPPED_KEY_NOT_FOUND");

            // Attempting to step receiving chain behind current counter must fail
            var behindCounterAttempt = bobSession.StepReceivingChain(_engine, targetCounter: 2);
            behindCounterAttempt.IsFailure.Should().BeTrue();
            behindCounterAttempt.Error.Code.Should().Be("COUNTER_ALREADY_PASSED");

            // -------------------------------------------------------------------------
            // 5. CONTINUOUS BIDIRECTIONAL MULTI-TURN RATCHETING (20 Alternating Turns)
            // -------------------------------------------------------------------------
            ConversationScriptRunner.ExchangeAlternatingTurns(
                aliceSession, "Alice",
                bobSession, "Bob",
                turns: 20,
                _engine);

            // -------------------------------------------------------------------------
            // 6. TEARDOWN AND ZEROIZATION
            // -------------------------------------------------------------------------
            aliceSession.Zeroize();
            aliceSession.IsZeroized.Should().BeTrue();

            var failStep = aliceSession.StepSendingChain(_engine);
            failStep.IsFailure.Should().BeTrue();
            failStep.Error.Code.Should().Be("INVALID_SESSION_STATE");
        }
    }

    [Test]
    public void DirectSession_CrossDhEpoch_OutOfOrderDelivery_StagesPreviousChainViaPN_AndDecryptsDelayedPackets()
    {
        using var alice = ScenarioParticipant.Create("Alice", _engine);
        using var bob = ScenarioParticipant.Create("Bob", _engine);

        var (aliceSession, bobSession) = ScenarioSessionPairBuilder
            .Between(alice, bob, _engine)
            .ViaDirectX3dh();

        using (aliceSession)
        using (bobSession)
        {
            // 1. Alice sends 3 messages in Epoch 0; Bob receives only message 0 (messages 1 and 2 are delayed in P2P transit)
            var a0 = WirePacketSimulator.PackDirect(aliceSession, "Alice Epoch0 Msg0", _engine);
            var a1Delayed = WirePacketSimulator.PackDirect(aliceSession, "Alice Epoch0 Msg1 (Delayed)", _engine);
            var a2Delayed = WirePacketSimulator.PackDirect(aliceSession, "Alice Epoch0 Msg2 (Delayed)", _engine);

            WirePacketSimulator.UnpackDirect(bobSession, a0, _engine).Should().Be("Alice Epoch0 Msg0");

            // 2. Bob replies to Alice (stepping his DH ratchet), and Alice receives Bob's reply
            var b0 = WirePacketSimulator.PackDirect(bobSession, "Bob Reply 0", _engine);
            WirePacketSimulator.UnpackDirect(aliceSession, b0, _engine).Should().Be("Bob Reply 0");

            // 3. Alice sends 2 messages in Epoch 1 (with PN = 3!). Bob receives Epoch 1 Msg 1 BEFORE Epoch 0 Msg 1 & 2!
            var aEpoch1Msg0Delayed = WirePacketSimulator.PackDirect(aliceSession, "Alice Epoch1 Msg0 (Delayed)", _engine);
            var aEpoch1Msg1 = WirePacketSimulator.PackDirect(aliceSession, "Alice Epoch1 Msg1", _engine);

            WirePacketSimulator.UnpackDirect(bobSession, aEpoch1Msg1, _engine).Should().Be("Alice Epoch1 Msg1");

            // 4. Now the delayed packets from both Epoch 0 (a1Delayed, a2Delayed) and Epoch 1 (aEpoch1Msg0Delayed) arrive!
            WirePacketSimulator.UnpackDirect(bobSession, a1Delayed, _engine).Should().Be("Alice Epoch0 Msg1 (Delayed)");
            WirePacketSimulator.UnpackDirect(bobSession, aEpoch1Msg0Delayed, _engine).Should().Be("Alice Epoch1 Msg0 (Delayed)");
            WirePacketSimulator.UnpackDirect(bobSession, a2Delayed, _engine).Should().Be("Alice Epoch0 Msg2 (Delayed)");
        }
    }
}
