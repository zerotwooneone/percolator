using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.IntegrationTests.Builders;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests;

[TestFixture]
public sealed class AdvancedSessionScenarioTests
{
    private ScenarioCryptoEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new ScenarioCryptoEngine();
    }

    [Test]
    public void MaxSkipThreshold_Exceeded_RejectsWithoutDerivation_DoSDefense()
    {
        using var alice = ScenarioParticipant.Create("Alice", _engine);
        using var bob = ScenarioParticipant.Create("Bob", _engine);

        var (aliceSession, bobSession) = ScenarioSessionPairBuilder
            .Between(alice, bob, _engine)
            .ViaDirectX3dh();

        using (aliceSession)
        using (bobSession)
        {
            // Bob's receiving counter starts at 0.
            // Attacker or desynchronized peer claims to send counter 2001 (threshold is 2000).
            const uint attackCounter = DirectRatchetSession.MaxSkipThreshold + 1;

            var result = bobSession.StepReceivingChain(_engine, targetCounter: attackCounter);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("SKIP_THRESHOLD_EXCEEDED");
            bobSession.ReceivingCounter.Should().Be(0, "Receiving counter must remain unchanged on rejected skip attempt.");
        }
    }

    [Test]
    public void SkippedKey_BoundedLruEviction_EvictsOldestWhenLimitExceeded()
    {
        using var alice = ScenarioParticipant.Create("Alice", _engine);
        using var bob = ScenarioParticipant.Create("Bob", _engine);

        var (aliceSession, bobSession) = ScenarioSessionPairBuilder
            .Between(alice, bob, _engine)
            .ViaDirectX3dh();

        using (aliceSession)
        using (bobSession)
        {
            // Advance directly to counter 1050 (MaxTotalSkippedKeys is 1000).
            // This skips keys 0..1049 (1050 skipped keys total).
            const uint targetCounter = 1050;
            var stepResult = bobSession.StepReceivingChain(_engine, targetCounter: targetCounter);
            stepResult.IsSuccess.Should().BeTrue();
            using (stepResult.Value.Key) { }

            bobSession.ReceivingCounter.Should().Be(targetCounter + 1);

            // Keys 0..49 must have been evicted to preserve the 1000 key limit.
            for (uint i = 0; i < 50; i++)
            {
                var evicted = bobSession.TryGetSkippedKey(i);
                evicted.IsFailure.Should().BeTrue($"Key {i} should be evicted by LRU bounded cache.");
                evicted.Error.Code.Should().Be("SKIPPED_KEY_NOT_FOUND");
            }

            // Keys 50..1049 should still be accessible.
            var retainedKey50 = bobSession.TryGetSkippedKey(50);
            retainedKey50.IsSuccess.Should().BeTrue("Key 50 should be retained in bounded LRU window.");
            retainedKey50.Value.Dispose();

            var retainedKey1049 = bobSession.TryGetSkippedKey(1049);
            retainedKey1049.IsSuccess.Should().BeTrue("Key 1049 should be retained in bounded LRU window.");
            retainedKey1049.Value.Dispose();
        }
    }

    [Test]
    public void MultiDevice_OneToNFanOut_IndependentRatcheting()
    {
        // -------------------------------------------------------------------------
        // 1. SETUP: Bob has Desktop (primary) and Mobile (secondary) devices
        // -------------------------------------------------------------------------
        using var alice = ScenarioParticipant.Create("Alice", _engine);
        using var bobDesktop = ScenarioParticipant.Create("Bob", _engine, DeviceId.Primary);
        using var bobMobile = bobDesktop.CreateAdditionalDevice("Mobile", new DeviceId(2), _engine);

        // Bob's devices share IdentityKey but have distinct DeviceIds and signed pre-keys
        bobDesktop.IdentityPublicKey.Should().Be(bobMobile.IdentityPublicKey);
        bobDesktop.DeviceId.Should().NotBe(bobMobile.DeviceId);

        // -------------------------------------------------------------------------
        // 2. ALICE ESTABLISHES SESSIONS TO BOTH OF BOB'S DEVICES
        // -------------------------------------------------------------------------
        var (aliceToDesktop, bobDesktopSession) = ScenarioSessionPairBuilder
            .Between(alice, bobDesktop, _engine)
            .ViaDirectX3dh();

        var (aliceToMobile, bobMobileSession) = ScenarioSessionPairBuilder
            .Between(alice, bobMobile, _engine)
            .ViaDirectX3dh();

        using (aliceToDesktop)
        using (bobDesktopSession)
        using (aliceToMobile)
        using (bobMobileSession)
        {
            // -------------------------------------------------------------------------
            // 3. FAN-OUT: Alice sends the same announcement to both devices
            // -------------------------------------------------------------------------
            const string broadcastContent = "Urgent: System upgrade at 22:00 UTC";

            var packetDesktop = WirePacketSimulator.PackDirect(aliceToDesktop, broadcastContent, _engine);
            var packetMobile = WirePacketSimulator.PackDirect(aliceToMobile, broadcastContent, _engine);

            // Both devices decrypt independently
            var textOnDesktop = WirePacketSimulator.UnpackDirect(bobDesktopSession, packetDesktop, _engine);
            var textOnMobile = WirePacketSimulator.UnpackDirect(bobMobileSession, packetMobile, _engine);

            textOnDesktop.Should().Be(broadcastContent);
            textOnMobile.Should().Be(broadcastContent);

            // -------------------------------------------------------------------------
            // 4. ASYMMETRIC REPLY: Mobile replies to Alice, causing DH ratchet step
            // -------------------------------------------------------------------------
            var mobileReplyPacket = WirePacketSimulator.PackDirect(bobMobileSession, "Acknowledged from Mobile.", _engine);
            var receivedOnAlice = WirePacketSimulator.UnpackDirect(aliceToMobile, mobileReplyPacket, _engine);
            receivedOnAlice.Should().Be("Acknowledged from Mobile.");

            // Alice's session to Desktop remains undisturbed
            aliceToDesktop.ReceivingCounter.Should().Be(0);
            aliceToMobile.ReceivingCounter.Should().Be(1);
        }
    }

    [Test]
    public void DirectRatchetSession_HydrationRoundTrip_PreservesRatchetingState()
    {
        using var alice = ScenarioParticipant.Create("Alice", _engine);
        using var bob = ScenarioParticipant.Create("Bob", _engine);

        var (aliceSession, bobSession) = ScenarioSessionPairBuilder
            .Between(alice, bob, _engine)
            .ViaDirectX3dh();

        using (bobSession)
        {
            // 1. Exchange 4 conversational turns
            for (int i = 0; i < 4; i++)
            {
                var msgA = $"Message {i} from Alice";
                var pA = WirePacketSimulator.PackDirect(aliceSession, msgA, _engine);
                WirePacketSimulator.UnpackDirect(bobSession, pA, _engine).Should().Be(msgA);

                var msgB = $"Reply {i} from Bob";
                var pB = WirePacketSimulator.PackDirect(bobSession, msgB, _engine);
                WirePacketSimulator.UnpackDirect(aliceSession, pB, _engine).Should().Be(msgB);
            }

            // 2. Capture snapshot of Alice's session state
            var snapshotSessionId = aliceSession.Id;
            var ownerId = aliceSession.OwnerIdentityId;
            var ownerDevice = aliceSession.OwnerDeviceId;
            var remoteId = aliceSession.RemotePeerId;
            var remoteDevice = aliceSession.RemoteDeviceId;
            var rootKey = aliceSession.RootKey != null ? ChainKey.FromSpan(aliceSession.RootKey.Span) : null;
            var sendingChain = aliceSession.SendingChainKey != null ? ChainKey.FromSpan(aliceSession.SendingChainKey.Span) : null;
            var receivingChain = aliceSession.ReceivingChainKey != null ? ChainKey.FromSpan(aliceSession.ReceivingChainKey.Span) : null;
            var remoteEphem = aliceSession.RemoteEphemeralPublicKey;
            var localEphemPriv = aliceSession.LocalEphemeralPrivateKey != null ? EphemeralPrivateKey.FromSpan(aliceSession.LocalEphemeralPrivateKey.Span) : null;
            var localEphemPub = aliceSession.LocalEphemeralPublicKey;
            var sendCounter = aliceSession.SendingCounter;
            var recvCounter = aliceSession.ReceivingCounter;
            var skippedKeys = aliceSession.SkippedMessageKeys.ToDictionary(k => k.Key, v => v.Value);

            // Zeroize old session (simulating app shutdown / memory eviction)
            aliceSession.Zeroize();
            aliceSession.IsZeroized.Should().BeTrue();

            // 3. Hydrate a brand new DirectRatchetSession from the snapshot
            using var hydratedAliceSession = new DirectRatchetSession(
                ownerId,
                ownerDevice,
                remoteId,
                remoteDevice,
                rootKey: rootKey,
                sendingChainKey: sendingChain,
                receivingChainKey: receivingChain,
                remoteEphemeralPublicKey: remoteEphem,
                localEphemeralPrivateKey: localEphemPriv,
                localEphemeralPublicKey: localEphemPub,
                sendingCounter: sendCounter,
                receivingCounter: recvCounter,
                sessionId: snapshotSessionId,
                skippedKeys: skippedKeys);

            rootKey?.Dispose();
            sendingChain?.Dispose();
            receivingChain?.Dispose();
            localEphemPriv?.Dispose();

            // 4. Continue communication seamlessly from hydrated session
            const string postHydrationMessage = "Turn 5 post-hydration message from Alice";
            var packetPostHydrate = WirePacketSimulator.PackDirect(hydratedAliceSession, postHydrationMessage, _engine);
            var unpackedByBob = WirePacketSimulator.UnpackDirect(bobSession, packetPostHydrate, _engine);

            unpackedByBob.Should().Be(postHydrationMessage);

            // Bob replies and hydrated Alice session decrypts cleanly
            const string bobFinalReply = "Session hydration verified successfully, Alice.";
            var packetBobFinal = WirePacketSimulator.PackDirect(bobSession, bobFinalReply, _engine);
            var unpackedByAlice = WirePacketSimulator.UnpackDirect(hydratedAliceSession, packetBobFinal, _engine);

            unpackedByAlice.Should().Be(bobFinalReply);
        }
    }
}
