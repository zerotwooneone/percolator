using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ports;
using Percolator.Application2IntegrationTests.Builders;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using DeliveryRoute = Percolator.PluginSdk.DeliveryRoute;

namespace Percolator.Application2IntegrationTests;

[TestFixture]
public sealed class ProfileIntegrationScenarioTests
{
    [Test]
    public async Task Phase1_Alice_And_Bob_DirectProfileExchange_Over_LoopbackPorts()
    {
        // ----------------------------------------------------------------------------------
        // Arrange:
        // 1. Build Alice (15001) and Bob (15002) via TestClusterBuilder
        // ----------------------------------------------------------------------------------
        var alicePort = new EndpointPort(15001);
        var bobPort = new EndpointPort(15002);

        var cluster = new TestClusterBuilder()
            .AddNode("Alice", alicePort)
            .AddNode("Bob", bobPort)
            .Build();

        var alice = cluster["Alice"];
        var bob = cluster["Bob"];

        // 2. Establish mutual direct session (contacts, direct channels, X3DH session, open loopback stream)
        var channelId = await alice.EstablishDirectSessionWithAsync(bob);
        cluster.Switch.HasActiveStream(alice.Port, bob.Port).Should().BeTrue();

        // Bob has a local nickname for Alice in his contact book
        var bobContactBefore = await bob.ContactQueries.GetContactDetailAsync(bob.IdentityId, alice.IdentityId);
        bobContactBefore.Should().NotBeNull();
        bobContactBefore!.DisplayName.Should().Be("Alice");

        // ----------------------------------------------------------------------------------
        // Act 1: Alice stages & publishes profile locally, then shares profile capability
        // with Bob over direct P2P stream
        // ----------------------------------------------------------------------------------
        const string aliceDisplayName = "Alice Wonderland";
        const string aliceBio = "Curiouser and curiouser!";
        const uint rev1 = 1;

        var publishResult = await alice.PublishProfileDirectAsync(aliceDisplayName, aliceBio, revision: rev1);
        publishResult.IsSuccess.Should().BeTrue();

        var shareResult = await alice.ShareProfileCapabilityAsync(channelId, bob, DeliveryRoute.DirectP2P, revision: rev1);
        shareResult.IsSuccess.Should().BeTrue();

        // ----------------------------------------------------------------------------------
        // Assert 1: Bob receives capability, fetches unauthenticated from Alice's profile store,
        // decrypts AEAD package, and persists remote profile into PeerProfileRepo
        // ----------------------------------------------------------------------------------
        var bobStoredProfile = await bob.PeerProfileRepo.GetByPeerIdAsync(alice.IdentityId);
        bobStoredProfile.Should().NotBeNull();
        bobStoredProfile!.DisplayName.Should().Be(aliceDisplayName);
        bobStoredProfile.StatusBio.Should().Be(aliceBio);
        bobStoredProfile.Revision.Should().Be(rev1);

        // Multi-Source Profile Storage: Bob records snapshot from DirectP2P endpoint
        var bobSnapshots = await bob.PeerProfileRepo.GetSourceSnapshotsAsync(alice.IdentityId);
        bobSnapshots.Should().HaveCount(1);
        bobSnapshots[0].SourceEndpoint.Should().Be($"localhost:{alice.Port.Value}");
        bobSnapshots[0].SourceType.Should().Be(ProfileSourceType.DirectP2P);
        bobSnapshots[0].Metadata.DisplayName.Should().Be(aliceDisplayName);

        // Sealed Sender Delivery Capability: Bob derived and stored mailbox routing info
        var bobRoutingCap = await bob.PeerMailboxRoutingRepo.GetByPeerIdAsync(alice.IdentityId);
        bobRoutingCap.Should().NotBeNull();
        bobRoutingCap!.RoutingToken.Should().Be(alice.MailboxRoutingToken);
        var expectedDeliveryToken = DeliveryToken.DeriveFromProfileKey(alice.IdentityPublicKey, alice.ProfileKey, alice.CryptoEngine);
        bobRoutingCap.DeliveryToken.Should().Be(expectedDeliveryToken);

        // Strict CQRS: Query Bob's PeerProfileQueries read model
        var bobProfileQuery = await bob.PeerProfileQueries.GetProfileAsync(alice.IdentityId);
        bobProfileQuery.Should().NotBeNull();
        bobProfileQuery!.DisplayName.Should().Be(aliceDisplayName);
        bobProfileQuery.StatusBio.Should().Be(aliceBio);
        bobProfileQuery.Revision.Should().Be(rev1);
        bobProfileQuery.HasAvatar.Should().BeFalse();

        // Critical Security & Privacy Requirement: Bob's local nickname must NEVER be overwritten
        var bobContactAfter = await bob.ContactQueries.GetContactDetailAsync(bob.IdentityId, alice.IdentityId);
        bobContactAfter.Should().NotBeNull();
        bobContactAfter!.DisplayName.Should().Be("Alice", "Local contact nickname must remain immutable against remote profile sync");

        // ----------------------------------------------------------------------------------
        // Act 2 & Assert 2: Conditional Fetch Verification
        // Alice re-sends identical capability (rev: 1); Bob skips redundant network fetch
        // ----------------------------------------------------------------------------------
        var resendResult = await alice.ShareProfileCapabilityAsync(channelId, bob, DeliveryRoute.DirectP2P, revision: rev1);
        resendResult.IsSuccess.Should().BeTrue();

        var bobProfileAfterResend = await bob.PeerProfileRepo.GetByPeerIdAsync(alice.IdentityId);
        bobProfileAfterResend.Should().NotBeNull();
        bobProfileAfterResend!.Revision.Should().Be(rev1);

        // ----------------------------------------------------------------------------------
        // Act 3 & Assert 3: Revision Increment & In-Place Update
        // Alice updates profile to Revision 2; Bob detects newer revision and updates profile
        // ----------------------------------------------------------------------------------
        const string aliceNewDisplayName = "Alice Liddell";
        const string aliceNewBio = "Through the Looking-Glass";
        const uint rev2 = 2;

        var publishV2 = await alice.PublishProfileDirectAsync(aliceNewDisplayName, aliceNewBio, revision: rev2);
        publishV2.IsSuccess.Should().BeTrue();

        var shareV2 = await alice.ShareProfileCapabilityAsync(channelId, bob, DeliveryRoute.DirectP2P, revision: rev2);
        shareV2.IsSuccess.Should().BeTrue();

        var bobProfileV2 = await bob.PeerProfileQueries.GetProfileAsync(alice.IdentityId);
        bobProfileV2.Should().NotBeNull();
        bobProfileV2!.DisplayName.Should().Be(aliceNewDisplayName);
        bobProfileV2.StatusBio.Should().Be(aliceNewBio);
        bobProfileV2.Revision.Should().Be(rev2);

        // Local nickname still preserved
        var bobContactAfterV2 = await bob.ContactQueries.GetContactDetailAsync(bob.IdentityId, alice.IdentityId);
        bobContactAfterV2!.DisplayName.Should().Be("Alice");
    }

    [Test]
    public async Task Phase2_Alice_And_Charlie_RelayedProfileExchange_Over_RelayMailbox()
    {
        // ----------------------------------------------------------------------------------
        // Arrange:
        // 1. Build cluster with Alice (15001), Charlie (15004), and zero-knowledge Relay (15003)
        // ----------------------------------------------------------------------------------
        var alicePort = new EndpointPort(15001);
        var relayPort = new EndpointPort(15003);
        var charliePort = new EndpointPort(15004);

        var cluster = new TestClusterBuilder()
            .AddNode("Alice", alicePort)
            .AddRelayNode("Relay", relayPort)
            .AddNode("Charlie", charliePort)
            .Build();

        var alice = cluster["Alice"];
        var charlie = cluster["Charlie"];
        var relay = cluster.GetRelay("Relay");

        // Establish mutual approved contacts, direct channel, and relayed session via Base64 invite & prekey bundle
        var channelId = await alice.EstablishRelayedSessionWithAsync(charlie, relay);

        // Charlie has a local nickname for Alice
        var charlieContactBefore = await charlie.ContactQueries.GetContactDetailAsync(charlie.IdentityId, alice.IdentityId);
        charlieContactBefore.Should().NotBeNull();
        charlieContactBefore!.DisplayName.Should().Be("Alice");

        // ----------------------------------------------------------------------------------
        // Act 1: Alice uploads encrypted profile package to zero-knowledge Relay
        // ----------------------------------------------------------------------------------
        const string aliceRelayedName = "Alice In Relayland";
        const string aliceRelayedBio = "Zero-Knowledge Profile";
        const uint rev1 = 1;

        var uploadResult = alice.PublishProfileToRelay(relay, aliceRelayedName, aliceRelayedBio, revision: rev1);
        uploadResult.IsSuccess.Should().BeTrue();

        // Verify relay hosted slot:
        // - Blinded locator matches HKDF derivation
        // - Ciphertext package is padded to 1024 bytes plaintext + 16 bytes auth tag = 1040 bytes
        var aliceLocator = BlindedProfileLocator.Derive(alice.IdentityPublicKey, alice.ProfileKey, alice.CryptoEngine);
        var relayFetchResult = relay.GetProfile(aliceLocator);
        relayFetchResult.IsSuccess.Should().BeTrue();
        relayFetchResult.Value.Ciphertext.Length.Should().Be(1040, "Profile ciphertext must be constant 1040 bytes (1024 padded + 16 tag)");

        // ----------------------------------------------------------------------------------
        // Act 2: Alice dispatches profile capability to Charlie via RelayedOneToOne
        // ----------------------------------------------------------------------------------
        var shareResult = await alice.ShareProfileCapabilityAsync(channelId, charlie, DeliveryRoute.RelayedOneToOne, revision: rev1);
        shareResult.IsSuccess.Should().BeTrue();

        // Alice outbox processes pending job to Relay mailbox
        var processedJobs = await alice.OutboxWorker.ProcessBatchAsync();
        processedJobs.Should().Be(1);
        relay.MailboxQueue.TotalCount.Should().Be(1);

        // Charlie drains mailbox and receives sealed capability envelope
        var drainedCount = await charlie.DrainMailboxAsync(relay);
        drainedCount.Should().Be(1);
        relay.MailboxQueue.TotalCount.Should().Be(0);

        // ----------------------------------------------------------------------------------
        // Assert: Charlie has fetched from Relay unauthenticated, decrypted, and saved profile
        // ----------------------------------------------------------------------------------
        var charlieProfile = await charlie.PeerProfileQueries.GetProfileAsync(alice.IdentityId);
        charlieProfile.Should().NotBeNull();
        charlieProfile!.DisplayName.Should().Be(aliceRelayedName);
        charlieProfile.StatusBio.Should().Be(aliceRelayedBio);
        charlieProfile.Revision.Should().Be(rev1);

        // Multi-Source Profile Storage: Charlie records snapshot from relay endpoint
        var charlieSnapshots = await charlie.PeerProfileRepo.GetSourceSnapshotsAsync(alice.IdentityId);
        charlieSnapshots.Should().HaveCount(1);
        charlieSnapshots[0].SourceEndpoint.Should().Be("relay");
        charlieSnapshots[0].SourceType.Should().Be(ProfileSourceType.Relay);
        charlieSnapshots[0].Metadata.DisplayName.Should().Be(aliceRelayedName);

        // Verify Charlie's local contact nickname is preserved
        var charlieContactAfter = await charlie.ContactQueries.GetContactDetailAsync(charlie.IdentityId, alice.IdentityId);
        charlieContactAfter!.DisplayName.Should().Be("Alice");
    }

    [Test]
    public async Task Phase3_Alice_KeyRotation_Revokes_Bob_While_Charlie_Receives_Update()
    {
        // ----------------------------------------------------------------------------------
        // Arrange:
        // Build Alice, Bob, Charlie and Relay.
        // Alice initially shares profile Rev 1 with both Bob and Charlie.
        // ----------------------------------------------------------------------------------
        var alicePort = new EndpointPort(15001);
        var bobPort = new EndpointPort(15002);
        var relayPort = new EndpointPort(15003);
        var charliePort = new EndpointPort(15004);

        var cluster = new TestClusterBuilder()
            .AddNode("Alice", alicePort)
            .AddNode("Bob", bobPort)
            .AddRelayNode("Relay", relayPort)
            .AddNode("Charlie", charliePort)
            .Build();

        var alice = cluster["Alice"];
        var bob = cluster["Bob"];
        var charlie = cluster["Charlie"];
        var relay = cluster.GetRelay("Relay");

        alice.RegisterMailbox(relay);
        bob.RegisterMailbox(relay);
        charlie.RegisterMailbox(relay);

        alice.AddPeerMailboxRoute(bob.IdentityId, $"localhost:{relay.Port.Value}", bob.MailboxRoutingToken, bob.MailboxDeliveryToken);
        bob.AddPeerMailboxRoute(alice.IdentityId, $"localhost:{relay.Port.Value}", alice.MailboxRoutingToken, alice.MailboxDeliveryToken);
        alice.AddPeerMailboxRoute(charlie.IdentityId, $"localhost:{relay.Port.Value}", charlie.MailboxRoutingToken, charlie.MailboxDeliveryToken);
        charlie.AddPeerMailboxRoute(alice.IdentityId, $"localhost:{relay.Port.Value}", alice.MailboxRoutingToken, alice.MailboxDeliveryToken);

        // Bootstrap sessions
        var aliceBobChannel = await alice.EstablishDirectSessionWithAsync(bob);
        var aliceCharlieChannel = await alice.EstablishDirectSessionWithAsync(charlie);

        // 1. Alice publishes initial profile (rev 1) to Relay
        const string initialName = "Alice Original";
        alice.PublishProfileToRelay(relay, initialName, "Original bio", revision: 1).IsSuccess.Should().BeTrue();

        // 2. Alice shares capability with both Bob and Charlie
        await alice.ShareProfileCapabilityAsync(aliceBobChannel, bob, DeliveryRoute.DirectP2P, revision: 1);
        await alice.ShareProfileCapabilityAsync(aliceCharlieChannel, charlie, DeliveryRoute.DirectP2P, revision: 1);

        (await bob.PeerProfileQueries.GetProfileAsync(alice.IdentityId))!.DisplayName.Should().Be(initialName);
        (await charlie.PeerProfileQueries.GetProfileAsync(alice.IdentityId))!.DisplayName.Should().Be(initialName);

        // Capture Bob's held stale keys & locators
        var staleProfileKey = alice.ProfileKey;
        var staleLocator = BlindedProfileLocator.Derive(alice.IdentityPublicKey, staleProfileKey, alice.CryptoEngine);
        var staleWriteToken = ProfileWriteToken.Derive(alice.IdentityPrivateKey, staleProfileKey, alice.CryptoEngine);

        // ----------------------------------------------------------------------------------
        // Act 1: Alice Revocation & Key Rotation
        // - Deletes old profile slot on Relay using write token
        // - Generates fresh ProfileKey
        // - Derives new BlindedProfileLocator and ProfileWriteToken
        // - Uploads new Revision 2 profile package under new locator
        // ----------------------------------------------------------------------------------
        var deleteResult = relay.DeleteProfile(staleLocator, staleWriteToken);
        deleteResult.IsSuccess.Should().BeTrue();

        alice.RotateProfileKey();
        var newProfileKey = alice.ProfileKey;
        var newLocator = BlindedProfileLocator.Derive(alice.IdentityPublicKey, newProfileKey, alice.CryptoEngine);
        var newWriteToken = ProfileWriteToken.Derive(alice.IdentityPrivateKey, newProfileKey, alice.CryptoEngine);

        newLocator.Should().NotBe(staleLocator);
        newWriteToken.Should().NotBe(staleWriteToken);

        const string rotatedName = "Alice Secure & Rotated";
        var uploadNewResult = alice.PublishProfileToRelay(relay, rotatedName, "Bob revoked", revision: 2);
        uploadNewResult.IsSuccess.Should().BeTrue();

        // ----------------------------------------------------------------------------------
        // Act 2: Alice shares fresh capability ONLY with approved contact Charlie
        // ----------------------------------------------------------------------------------
        var shareCharlieV2 = await alice.ShareProfileCapabilityAsync(aliceCharlieChannel, charlie, DeliveryRoute.DirectP2P, revision: 2);
        shareCharlieV2.IsSuccess.Should().BeTrue();

        // Charlie successfully updates to Revision 2
        var charlieUpdatedProfile = await charlie.PeerProfileQueries.GetProfileAsync(alice.IdentityId);
        charlieUpdatedProfile!.DisplayName.Should().Be(rotatedName);
        charlieUpdatedProfile.Revision.Should().Be(2);

        // ----------------------------------------------------------------------------------
        // Assert: Cryptographic Revocation Guarantees against Bob
        // ----------------------------------------------------------------------------------
        // 1. Bob attempting to fetch the old locator from Relay receives 404 / PROFILE_NOT_FOUND
        var bobFetchOldSlot = relay.GetProfile(staleLocator);
        bobFetchOldSlot.IsFailure.Should().BeTrue();
        bobFetchOldSlot.Error.Code.Should().Be("PROFILE_NOT_FOUND");

        // 2. Bob attempting to overwrite or delete with stale write token fails
        var bobUnauthorizedDelete = relay.DeleteProfile(staleLocator, staleWriteToken);
        bobUnauthorizedDelete.IsFailure.Should().BeTrue();

        // 3. Even if Bob intercepted the new locator, Bob's stale ProfileKey fails AEAD decryption
        var newSlotFromRelay = relay.GetProfile(newLocator);
        newSlotFromRelay.IsSuccess.Should().BeTrue();
        var decryptWithStaleKey = alice.ProfileManager.DecryptProfile(
            newSlotFromRelay.Value,
            staleProfileKey,
            alice.CryptoEngine,
            expectedRevision: 2);
        decryptWithStaleKey.IsFailure.Should().BeTrue("AEAD authentication tag must reject decryption with stale/revoked profile key");

        // 4. Sealed Sender DeliveryToken Authorization:
        // Relay rejects any mailbox envelope presented with an invalid or revoked delivery token
        var invalidDeliveryToken = DeliveryToken.FromSpan(new byte[32]);
        var fakeEnvelope = new MailboxEnvelope(
            EnvelopeId.New(),
            alice.MailboxRoutingToken,
            new byte[64],
            alice.TimeProvider.UtcNow,
            alice.TimeProvider.UtcNow.AddMinutes(5));

        var rejectedEnqueue = relay.MailboxQueue.Enqueue(fakeEnvelope, invalidDeliveryToken, alice.TimeProvider);
        rejectedEnqueue.IsFailure.Should().BeTrue();
        rejectedEnqueue.Error.Code.Should().Be("UNAUTHORIZED_DELIVERY_TOKEN");
    }
}
