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
}
