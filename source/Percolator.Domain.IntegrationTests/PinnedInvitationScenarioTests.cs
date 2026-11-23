using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.IntegrationTests.Builders;
using Percolator.Domain.IntegrationTests.TestDoubles;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.IntegrationTests;

[TestFixture]
public sealed class PinnedInvitationScenarioTests
{
    private ScenarioCryptoEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new ScenarioCryptoEngine();
    }

    [Test]
    public async Task PinnedInvitation_MultiplePeers_ConsumeDistinctOneTimePreKeys_Full4DhForwardSecrecy()
    {
        // 1. Bob hosts OPK bundle on Relay R1 and exports a single pinned-identity invitation
        var relayEndpoint = RelayEndpoint.Parse("percolator://relay1.example.com:5100");
        using var bob = ScenarioParticipant.Create("Bob", _engine, opkPoolSize: 10);
        var hostedBundle = bob.CreateHostedPreKeyBundle(_engine);
        hostedBundle.AvailableOneTimePreKeyCount.Should().Be(10);

        var invitation = bob.CreatePeerInvitation(relayEndpoint);

        // 2. Alice connects using Bob's invitation link
        using var alice = ScenarioParticipant.Create("Alice", _engine);
        var (aliceSession, bobAliceSession) = await ScenarioSessionPairBuilder
            .Between(alice, bob, _engine)
            .ViaPinnedPeerInvitationAsync(invitation, hostedBundle);

        hostedBundle.AvailableOneTimePreKeyCount.Should().Be(9, "Alice consumed exactly 1 OTPK.");

        // 3. Carol connects using the exact same invitation link
        using var carol = ScenarioParticipant.Create("Carol", _engine);
        var (carolSession, bobCarolSession) = await ScenarioSessionPairBuilder
            .Between(carol, bob, _engine)
            .ViaPinnedPeerInvitationAsync(invitation, hostedBundle);

        hostedBundle.AvailableOneTimePreKeyCount.Should().Be(8, "Carol consumed a distinct OTPK from the pool.");

        // 4. Verify symmetric Double Ratchet communication for Alice <-> Bob
        using (aliceSession)
        using (bobAliceSession)
        using (carolSession)
        using (bobCarolSession)
        {
            var msgAlice = WirePacketSimulator.PackDirect(aliceSession, "Hi Bob, I am Alice!", _engine);
            var decAlice = WirePacketSimulator.UnpackDirect(bobAliceSession, msgAlice, _engine);
            decAlice.Should().Be("Hi Bob, I am Alice!");

            var msgBobToAlice = WirePacketSimulator.PackDirect(bobAliceSession, "Welcome Alice!", _engine);
            var decBobToAlice = WirePacketSimulator.UnpackDirect(aliceSession, msgBobToAlice, _engine);
            decBobToAlice.Should().Be("Welcome Alice!");

            // 5. Verify symmetric Double Ratchet communication for Carol <-> Bob
            var msgCarol = WirePacketSimulator.PackDirect(carolSession, "Hi Bob, I am Carol!", _engine);
            var decCarol = WirePacketSimulator.UnpackDirect(bobCarolSession, msgCarol, _engine);
            decCarol.Should().Be("Hi Bob, I am Carol!");

            var msgBobToCarol = WirePacketSimulator.PackDirect(bobCarolSession, "Welcome Carol!", _engine);
            var decBobToCarol = WirePacketSimulator.UnpackDirect(carolSession, msgBobToCarol, _engine);
            decBobToCarol.Should().Be("Welcome Carol!");

            // 6. Verify cross-talk isolation: Alice cannot decrypt Bob's message to Carol
            var crossDecryptAttempt = () => WirePacketSimulator.UnpackDirect(aliceSession, msgBobToCarol, _engine);
            crossDecryptAttempt.Should().Throw<Exception>("Alice's session keys cannot decrypt messages intended for Carol.");
        }
    }

    [Test]
    public void PinnedInvitation_MaliciousRelaySwapsIdentityKey_RejectedBeforeSessionCreation()
    {
        var relayEndpoint = RelayEndpoint.Parse("percolator://relay1.example.com:5100");
        using var bob = ScenarioParticipant.Create("Bob", _engine, opkPoolSize: 2);
        using var attacker = ScenarioParticipant.Create("Attacker", _engine, opkPoolSize: 2);

        var invitation = bob.CreatePeerInvitation(relayEndpoint);

        // Malicious relay returns a bundle with Attacker's IdentityKey instead of Bob's
        var forgedBundle = attacker.CreatePreKeyBundle(opkId: 1);

        var verifyResult = invitation.VerifyFetchedBundle(forgedBundle, _engine);

        verifyResult.IsFailure.Should().BeTrue();
        verifyResult.Error.Code.Should().Be("IDENTITY_KEY_MISMATCH");
    }

    [Test]
    public void PinnedInvitation_MaliciousRelayForgesSignedPreKey_RejectedBeforeSessionCreation()
    {
        var relayEndpoint = RelayEndpoint.Parse("percolator://relay1.example.com:5100");
        using var bob = ScenarioParticipant.Create("Bob", _engine, opkPoolSize: 2);
        using var attacker = ScenarioParticipant.Create("Attacker", _engine);

        var invitation = bob.CreatePeerInvitation(relayEndpoint);

        // Attacker creates a fresh SPK not signed by Bob, but claims it belongs to Bob's IdentityKey
        var (forgedSpkPriv, forgedSpkPub) = _engine.GenerateEphemeralKeyPair();
        var forgedSignature = _engine.SignEd25519(attacker.IdentityPrivateKey.Span, forgedSpkPub.Span);

        var tamperedBundle = new PreKeyBundle(
            bob.DeviceId,
            bob.IdentityPublicKey, // Bob's identity key
            forgedSpkPub,          // But forged SPK
            DeviceLinkProof.FromSpan(forgedSignature), // Signed by Attacker, not Bob
            SignedPreKeyId.First);

        var verifyResult = invitation.VerifyFetchedBundle(tamperedBundle, _engine);

        verifyResult.IsFailure.Should().BeTrue();
        verifyResult.Error.Code.Should().Be("INVALID_PREKEY_SIGNATURE");
    }

    [Test]
    public async Task PinnedInvitation_SpkRotation_OldInvitationContinuesWorkingWithFreshSpk()
    {
        var relayEndpoint = RelayEndpoint.Parse("percolator://relay1.example.com:5100");
        using var bob = ScenarioParticipant.Create("Bob", _engine, opkPoolSize: 5);
        var hostedBundle = bob.CreateHostedPreKeyBundle(_engine);

        // 1. Bob exports an invitation
        var invitation = bob.CreatePeerInvitation(relayEndpoint);

        // 2. Bob rotates his SPK on the Exchange (simulating 48h/7d periodic SPK maintenance)
        var (newSpkPriv, newSpkPub) = _engine.GenerateEphemeralKeyPair();
        var newSig = _engine.SignEd25519(bob.IdentityPrivateKey.Span, newSpkPub.Span);
        var newSpkId = new SignedPreKeyId(2);

        // Store new SPK in Bob's prekey store so he can decrypt incoming handshakes
        await bob.PreKeyStore.StoreSignedPreKeyAsync(
            bob.IdentityId,
            bob.DeviceId,
            newSpkPriv,
            newSpkPub,
            DeviceLinkProof.FromSpan(newSig));

        var rotateResult = hostedBundle.UpdateSignedPreKey(
            newSpkPub,
            DeviceLinkProof.FromSpan(newSig),
            newSpkId,
            RelayHostingPolicy.Default,
            _engine);
        rotateResult.IsSuccess.Should().BeTrue();

        // 3. Alice arrives later with Bob's original invitation link
        using var alice = ScenarioParticipant.Create("Alice", _engine);
        var (aliceSession, bobSession) = await ScenarioSessionPairBuilder
            .Between(alice, bob, _engine)
            .ViaPinnedPeerInvitationAsync(invitation, hostedBundle);

        // 4. Verify ratchet works bidirectionally
        using (aliceSession)
        using (bobSession)
        {
            var packet = WirePacketSimulator.PackDirect(aliceSession, "Hi Bob after rotation!", _engine);
            var dec = WirePacketSimulator.UnpackDirect(bobSession, packet, _engine);
            dec.Should().Be("Hi Bob after rotation!");
        }
    }
}
