using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2IntegrationTests.Builders;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Apps.Discovery.Ingress;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2IntegrationTests;

[TestFixture]
public sealed class PeerDiscoveryScenarioTests
{
    [Test]
    public async Task Phase6_Step1_Directory_Visibility_Registration_And_Admission_Gates()
    {
        // ----------------------------------------------------------------------------------
        // Setup: Bob (15002), Relay (15003), and Dave (15005 as directory observer)
        // ----------------------------------------------------------------------------------
        var bobPort = new EndpointPort(15002);
        var relayPort = new EndpointPort(15003);
        var davePort = new EndpointPort(15005);

        var cluster = new TestClusterBuilder()
            .AddNode("Bob", bobPort)
            .AddRelayNode("Relay", relayPort)
            .AddNode("Dave", davePort)
            .Build();

        var bob = cluster["Bob"];
        var relay = cluster.GetRelay("Relay");
        var dave = cluster["Dave"];
        var serializer = new TestPayloadSerializer();
        var queryLimit = DiscoveryResultLimit.Create(10).Value;

        // ----------------------------------------------------------------------------------
        // Invariant 1: Anti-Impersonation Check
        // An attacker (Eve) attempting to alter Bob's discoverability is strictly rejected.
        // ----------------------------------------------------------------------------------
        var eveId = PublicIdentityId.New();
        var forgedPayload = new DiscoveryRegistrationPayload(bob.IdentityId, PeerDiscoveryVisibility.Discoverable);
        var forgedBody = serializer.Serialize(forgedPayload);
        var forgedFrame = new byte[1 + forgedBody.Length];
        forgedFrame[0] = (byte)DiscoveryOpcode.RegisterVisibility;
        forgedBody.Span.CopyTo(forgedFrame.AsSpan(1));

        var unauthContext = new InboundPayloadContext(
            ChannelId.New(),
            eveId, // Authenticated sender is Eve, but payload declares Bob
            DeviceId.Primary,
            AppId.Discovery,
            forgedFrame,
            bob.TimeProvider.UtcNow);

        var unauthResult = await relay.HandleDiscoveryInboundAsync(unauthContext);
        unauthResult.IsSuccess.Should().BeFalse();
        unauthResult.Error.Code.Should().Be("UNAUTHORIZED_REGISTRATION");

        // ----------------------------------------------------------------------------------
        // Invariant 2: Admission Gate - Missing Active Signed PreKey
        // A peer without active signed prekeys published to the relay cannot be admitted to discoverable listings.
        // ----------------------------------------------------------------------------------
        var preAdmissionReg = RelayPeerRegistration.Create(
            bob.IdentityId,
            PeerDiscoveryVisibility.Discoverable,
            hasAuthenticatedHandshake: true,
            hasActiveSignedPreKey: false,
            bob.TimeProvider.UtcNow).Value!;

        await relay.Directory.RegisterPeerAsync(preAdmissionReg);

        var preAdmissionDiscoverable = await dave.NearbyDiscovery.QueryNearbyPeersAsync(relay.RelayIdentityId, queryLimit);
        preAdmissionDiscoverable.Should().NotContain(p => p.PeerId == bob.IdentityId,
            "Peer without active signed prekey must fail admission gate evaluation");

        // ----------------------------------------------------------------------------------
        // Step 6.1: Bob publishes prekeys and registers as Discoverable on Relay
        // ----------------------------------------------------------------------------------
        bob.PublishHostedPreKeys(relay, oneTimeKeyCount: 5);

        var registerResult = await bob.RegisterDiscoveryVisibilityAsync(relay, PeerDiscoveryVisibility.Discoverable);
        registerResult.IsSuccess.Should().BeTrue();

        var discoverablePeers = await dave.NearbyDiscovery.QueryNearbyPeersAsync(relay.RelayIdentityId, queryLimit);
        discoverablePeers.Should().ContainSingle(p => p.PeerId == bob.IdentityId);

        // ----------------------------------------------------------------------------------
        // Step 6.1b: Bob switches to Stealth / Hidden mode
        // ----------------------------------------------------------------------------------
        var hideResult = await bob.RegisterDiscoveryVisibilityAsync(relay, PeerDiscoveryVisibility.Hidden);
        hideResult.IsSuccess.Should().BeTrue();

        var hiddenDiscoverablePeers = await dave.NearbyDiscovery.QueryNearbyPeersAsync(relay.RelayIdentityId, queryLimit);
        hiddenDiscoverablePeers.Should().NotContain(p => p.PeerId == bob.IdentityId,
            "Hidden peer must be excluded from discoverable listings");

        // ----------------------------------------------------------------------------------
        // Step 6.1c: Bob restores Discoverable mode
        // ----------------------------------------------------------------------------------
        var restoreResult = await bob.RegisterDiscoveryVisibilityAsync(relay, PeerDiscoveryVisibility.Discoverable);
        restoreResult.IsSuccess.Should().BeTrue();

        var restoredPeers = await dave.NearbyDiscovery.QueryNearbyPeersAsync(relay.RelayIdentityId, queryLimit);
        restoredPeers.Should().ContainSingle(p => p.PeerId == bob.IdentityId);
    }

    [Test]
    public async Task Phase6_Step2_Nearby_Discovery_Workflow_Discovers_And_Connects_Peer()
    {
        // ----------------------------------------------------------------------------------
        // Setup: Bob (15002), Dave (15005), and Relay (15003)
        // ----------------------------------------------------------------------------------
        var bobPort = new EndpointPort(15002);
        var davePort = new EndpointPort(15005);
        var relayPort = new EndpointPort(15003);

        var cluster = new TestClusterBuilder()
            .AddNode("Bob", bobPort)
            .AddNode("Dave", davePort)
            .AddRelayNode("Relay", relayPort)
            .Build();

        var bob = cluster["Bob"];
        var dave = cluster["Dave"];
        var relay = cluster.GetRelay("Relay");

        // Bob publishes 5 OTPKs to Relay and registers as Discoverable
        var bobHostedBundle = bob.PublishHostedPreKeys(relay, oneTimeKeyCount: 5);
        bobHostedBundle.AvailableOneTimePreKeyCount.Should().Be(5);
        await bob.RegisterDiscoveryVisibilityAsync(relay, PeerDiscoveryVisibility.Discoverable);

        // Dave registers mailbox on Relay
        dave.RegisterMailbox(relay);

        // ----------------------------------------------------------------------------------
        // Step 6.2a: Dave queries Relay via NearbyPeerDiscoveryWorkflow driving port
        // ----------------------------------------------------------------------------------
        var queryLimit = DiscoveryResultLimit.Create(10).Value;
        var candidates = await dave.NearbyDiscovery.QueryNearbyPeersAsync(relay.RelayIdentityId, queryLimit);

        candidates.Should().NotBeEmpty();
        candidates.Should().ContainSingle(c => c.PeerId == bob.IdentityId);
        var bobCandidate = candidates.Single(c => c.PeerId == bob.IdentityId);
        bobCandidate.Route.Should().Be(DeliveryRoute.RelayedOneToOne);

        // ----------------------------------------------------------------------------------
        // Step 6.2b: Dave connects to Bob via ConnectToDiscoveredPeerAsync using discovery metadata
        // Peer Isolation: Dave extracts routing tokens directly from discovery candidate descriptor
        // ----------------------------------------------------------------------------------
        bobCandidate.RoutingToken.Should().NotBeNull();
        bobCandidate.DeliveryToken.Should().NotBeNull();
        bobCandidate.RelayEndpoint.Should().NotBeNull();

        dave.AddPeerMailboxRoute(bobCandidate.PeerId, bobCandidate.RelayEndpoint!, bobCandidate.RoutingToken!.Value, bobCandidate.DeliveryToken!);
        bob.AddPeerMailboxRoute(dave.IdentityId, bobCandidate.RelayEndpoint!, dave.MailboxRoutingToken, dave.MailboxDeliveryToken);

        var connectResult = await dave.NearbyDiscovery.ConnectToDiscoveredPeerAsync(
            dave.IdentityId,
            dave.DeviceId,
            bobCandidate);

        connectResult.IsSuccess.Should().BeTrue();
        bobHostedBundle.AvailableOneTimePreKeyCount.Should().Be(4,
            "Consuming Bob's bundle during handshake initiation decrements OTPK count on Relay");

        var channelId = connectResult.Value.ChannelId;

        // ----------------------------------------------------------------------------------
        // Step 6.2c: Dave's OutboxWorker flushes handshake package to Relay
        // ----------------------------------------------------------------------------------
        var flushedJobs = await dave.OutboxWorker.ProcessBatchAsync();
        flushedJobs.Should().Be(1);
        relay.MailboxQueue.TotalCount.Should().Be(1);

        // ----------------------------------------------------------------------------------
        // Step 6.2d: Bob drains mailbox, verifies pending contact, and approves connection
        // ----------------------------------------------------------------------------------
        var drained = await bob.DrainMailboxAsync(relay);
        drained.Should().Be(1);

        var pendingContact = await bob.ContactQueries.GetContactDetailAsync(bob.IdentityId, dave.IdentityId);
        pendingContact.Should().NotBeNull();
        pendingContact!.State.Should().Be(ContactState.PendingApproval);

        await bob.ContactRequestCoordinator.ApproveRequestAsync(bob.IdentityId, dave.IdentityId, PeerTrustLevel.Verified);
        await bob.HandshakeService.CompletePendingHandshakeAsync(bob.IdentityId, dave.IdentityId);
        await bob.AddApprovedContactAsync(dave);
        await dave.AddApprovedContactAsync(bob);

        var mutualChannelId = await dave.EstablishDirectChannelWithAsync(bob);

        var approvedContact = await bob.ContactQueries.GetContactDetailAsync(bob.IdentityId, dave.IdentityId);
        approvedContact!.State.Should().Be(ContactState.Active);

        // ----------------------------------------------------------------------------------
        // Step 6.2e: Bidirectional Relayed Chat over the newly discovered session
        // ----------------------------------------------------------------------------------
        var sendCmd = new SendMessageCommand(
            mutualChannelId,
            bob.IdentityId,
            dave.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.RelayedOneToOne,
            "Hello Dave! Discovered through relay directory.");

        var sendResult = await bob.ChatService.SendMessageAsync(sendCmd);
        sendResult.IsSuccess.Should().BeTrue();

        await bob.OutboxWorker.ProcessBatchAsync();
        await dave.DrainMailboxAsync(relay);

        ulong nowMs = (ulong)dave.TimeProvider.UtcNow.ToUnixTimeMilliseconds();
        var daveMessages = await dave.ChatQueries.GetMessagesAsync(mutualChannelId, null, 50, nowMs);
        daveMessages.Should().ContainSingle(m => m.Content == "Hello Dave! Discovered through relay directory.");
    }

    [Test]
    public async Task Phase6_Step3_Blinded_Rendezvous_Matching_Establishes_Direct_Session()
    {
        // ----------------------------------------------------------------------------------
        // Setup: Alice (15001), Dave (15005), and Relay (15003)
        // ----------------------------------------------------------------------------------
        var alicePort = new EndpointPort(15001);
        var davePort = new EndpointPort(15005);
        var relayPort = new EndpointPort(15003);

        var cluster = new TestClusterBuilder()
            .AddNode("Alice", alicePort)
            .AddNode("Dave", davePort)
            .AddRelayNode("Relay", relayPort)
            .Build();

        var alice = cluster["Alice"];
        var dave = cluster["Dave"];
        var relay = cluster.GetRelay("Relay");
        var serializer = new TestPayloadSerializer();

        // Alice and Dave establish an out-of-band shared secret using strong value object
        var sharedSecret = RendezvousSharedSecret.Generate();

        var epoch = new DiscoveryEpoch(25000);

        // ----------------------------------------------------------------------------------
        // Step 6.3a: Alice derives blinded epoch locator and encrypts rendezvous route
        // Route binds BlindedLocator as AEAD Associated Authenticated Data (AAD)
        // ----------------------------------------------------------------------------------
        var locator = alice.RendezvousService.DeriveLocator(alice.IdentityId, sharedSecret, epoch);
        var routeDesc = new RelayRouteDescriptor($"localhost:{alice.Port.Value}");
        var encryptedRoute = EncryptedRendezvousRoute.Encrypt(routeDesc, locator, sharedSecret, epoch, alice.IdentityId);

        // Alice publishes encrypted rendezvous route to Relay
        var publishPayload = new BlindedRendezvousPublishPayload(locator, encryptedRoute);
        var pubBody = serializer.Serialize(publishPayload);
        var pubFrame = new byte[1 + pubBody.Length];
        pubFrame[0] = (byte)DiscoveryOpcode.RendezvousPublish;
        pubBody.Span.CopyTo(pubFrame.AsSpan(1));

        var pubContext = new InboundPayloadContext(
            ChannelId.New(),
            alice.IdentityId,
            alice.DeviceId,
            AppId.Discovery,
            pubFrame,
            alice.TimeProvider.UtcNow);

        var pubResult = await relay.HandleDiscoveryInboundAsync(pubContext);
        pubResult.IsSuccess.Should().BeTrue();

        // ----------------------------------------------------------------------------------
        // Step 6.3b: Dave derives 3-epoch sliding window and queries Relay for matching locator
        // ----------------------------------------------------------------------------------
        var queryWindow = dave.RendezvousService.DeriveLocatorWindow(alice.IdentityId, sharedSecret, epoch);
        queryWindow.Count.Should().Be(3, "Sliding query window covers current epoch +/- 1");

        var queryPayload = new BlindedRendezvousQueryPayload(queryWindow);
        var queryContext = new InboundPayloadContext(
            ChannelId.New(),
            dave.IdentityId,
            dave.DeviceId,
            AppId.Discovery,
            ReadOnlyMemory<byte>.Empty,
            dave.TimeProvider.UtcNow);

        var queryResponse = await relay.DiscoveryHandler.HandleRendezvousQueryAsync(queryContext, queryPayload);
        queryResponse.IsSuccess.Should().BeTrue();
        queryResponse.Value.Matches.Should().ContainSingle();

        var match = queryResponse.Value.Matches[0];
        match.Locator.Should().Be(locator);

        // ----------------------------------------------------------------------------------
        // Step 6.3c: Dave decrypts rendezvous route using shared secret and Alice's identity
        // ----------------------------------------------------------------------------------
        var decryptResult = match.EncryptedRoute.Decrypt(locator, sharedSecret, epoch, alice.IdentityId);
        decryptResult.IsSuccess.Should().BeTrue();
        decryptResult.Value.Should().Be(routeDesc);

        // Security Invariant: Corrupted secret or third party fails AEAD authentication
        var corruptedBytes = sharedSecret.Span.ToArray();
        corruptedBytes[0] ^= 0xFF;
        var wrongSecret = new RendezvousSharedSecret(corruptedBytes);
        var failedDecrypt = match.EncryptedRoute.Decrypt(locator, wrongSecret, epoch, alice.IdentityId);
        failedDecrypt.IsSuccess.Should().BeFalse();
        failedDecrypt.Error.Code.Should().Be("RENDEZVOUS_DECRYPTION_FAILED");

        // ----------------------------------------------------------------------------------
        // Step 6.3d: Dave connects direct P2P session using resolved endpoint from route descriptor
        // ----------------------------------------------------------------------------------
        var endpointParts = decryptResult.Value.Value.Split(':');
        var targetPort = new EndpointPort(int.Parse(endpointParts[1]));
        cluster.Switch.OpenDirectStream(dave.Port, targetPort);
        // Step 6.3d: Dave establishes direct session with Alice and exchanges chat
        // ----------------------------------------------------------------------------------
        var channelId = await alice.EstablishDirectSessionWithAsync(dave);

        var sendCmd = new SendMessageCommand(
            channelId,
            alice.IdentityId,
            dave.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.DirectP2P,
            "Rendezvous matched! Direct connection active.");

        var sendResult = await alice.ChatService.SendMessageAsync(sendCmd);
        sendResult.IsSuccess.Should().BeTrue();

        ulong nowMs = (ulong)dave.TimeProvider.UtcNow.ToUnixTimeMilliseconds();
        var daveMessages = await dave.ChatQueries.GetMessagesAsync(channelId, null, 50, nowMs);
        daveMessages.Should().ContainSingle(m => m.Content == "Rendezvous matched! Direct connection active.");
    }
}
