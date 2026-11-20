using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.IntegrationTests.Builders;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests;

[TestFixture]
public sealed class RelayedSessionScenarioTests
{
    private ScenarioCryptoEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new ScenarioCryptoEngine();
    }

    [Test]
    public async Task RelayedSession_PreKeyDirectory_AtomicConsumption_And_AsymmetricChains()
    {
        // -------------------------------------------------------------------------
        // 1. SETUP BOB WITH ONE-TIME PRE-KEY POOL & HOST ON RELAY DIRECTORY
        // -------------------------------------------------------------------------
        using var bob = ScenarioParticipant.Create("Bob", _engine, opkPoolSize: 3);
        var hostedBundle = bob.CreateHostedPreKeyBundle(_engine);
        hostedBundle.AvailableOneTimePreKeyCount.Should().Be(3);

        // -------------------------------------------------------------------------
        // 2. ALICE ESTABLISHES RELAYED 4-DH X3DH SESSION VIA HOSTED OPK BUNDLE
        // -------------------------------------------------------------------------
        using var alice = ScenarioParticipant.Create("Alice", _engine);

        var (aliceSession, bobSession) = await ScenarioSessionPairBuilder
            .Between(alice, bob, _engine)
            .ViaRelayedOpkAsync(hostedBundle);

        hostedBundle.AvailableOneTimePreKeyCount.Should().Be(2, "Relay dequeues the OPK upon dispensing.");

        using (aliceSession)
        using (bobSession)
        {
            // -------------------------------------------------------------------------
            // 3. INITIAL RELAYED PACKET DISPATCH AND DECRYPTION
            // -------------------------------------------------------------------------
            const string initialPlaintext = "Hello Bob! Initiating relayed 1:1 session via OPK-1.";
            var initialPacket = WirePacketSimulator.PackDirect(aliceSession, initialPlaintext, _engine);
            var decryptedInitial = WirePacketSimulator.UnpackDirect(bobSession, initialPacket, _engine);

            decryptedInitial.Should().Be(initialPlaintext);

            // -------------------------------------------------------------------------
            // 4. VERIFY SINGLE-USE OF ONE-TIME PRE-KEY (REPLAY PREVENTION)
            // -------------------------------------------------------------------------
            // Attempting to consume OPK 1 a second time must fail
            var secondConsumption = await bob.PreKeyStore.TryConsumeOneTimePreKeyPrivateAsync(bob.IdentityId, bob.DeviceId, 1u);
            secondConsumption.Should().BeNull("One-time prekeys must be strictly consumed once to guarantee forward secrecy.");

            // -------------------------------------------------------------------------
            // 5. BOB RESPONDS OVER RELAY (SYMMETRIC TO ASYMMETRIC DH RATCHET TRANSITION)
            // -------------------------------------------------------------------------
            const string replyPlaintext = "Acknowledged Alice. OPK-1 consumed and destroyed. Session secure.";
            var replyPacket = WirePacketSimulator.PackDirect(bobSession, replyPlaintext, _engine);
            replyPacket.EphemeralPublicKey.Should().NotBeNull("Bob's reply must generate a new ratchet ephemeral key.");

            var decryptedReply = WirePacketSimulator.UnpackDirect(aliceSession, replyPacket, _engine);
            decryptedReply.Should().Be(replyPlaintext);

            // -------------------------------------------------------------------------
            // 6. MULTI-TURN RELAY CONVERSATION CONTINUITY
            // -------------------------------------------------------------------------
            ConversationScriptRunner.ExchangeAlternatingTurns(
                aliceSession, "Alice",
                bobSession, "Bob",
                turns: 10,
                _engine);
        }
    }
}
