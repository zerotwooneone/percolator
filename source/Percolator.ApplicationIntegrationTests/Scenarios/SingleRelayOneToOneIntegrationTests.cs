namespace Percolator.ApplicationIntegrationTests.Scenarios;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.ApplicationIntegrationTests.Harness;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.PluginSdk.Profiles;
using Microsoft.Extensions.DependencyInjection;

[TestFixture]
public sealed class SingleRelayOneToOneIntegrationTests
{
    private TestNetworkTopology _topology = null!;

    [SetUp]
    public void SetUp()
    {
        _topology = new TestNetworkTopology();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _topology.DisposeAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task Handshake_ChoiceA_AuthToSealedSenderTransition_And_SymmetricDoubleRatchet_Exchange()
    {
        // 1. Start Relay R1
        var relay = await _topology.CreateRelayAsync().ConfigureAwait(false);

        // 2. Provision Personas Alice and Bob on R1
        var alice = await _topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await _topology.CreatePersonaAsync("Bob").ConfigureAwait(false);

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);

        // 3. Bob exports 4-field PeerInvitation pointing to R1 with pinned IdentityKey
        var inviteUri = await bob.CreateInvitationAsync(relay).ConfigureAwait(false);
        inviteUri.Should().StartWith("percolator://invite/");

        // 4. Alice connects using the invitation (Authenticated FetchPreKeyBundle -> Pinned Verify -> Authenticated SendInitialHandshake)
        var aliceChannelId = await alice.ConnectWithInvitationAsync(inviteUri).ConfigureAwait(false);
        aliceChannelId.Should().NotBeNull();

        // 5. Bob drains from R1, receives inbound contact request
        var contactReq = await bob.WaitForInboundContactRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        contactReq.OwnerIdentityId.Should().Be(bob.IdentityId);
        contactReq.RemotePeerId.Should().Be(alice.IdentityId);

        // 6. Bob approves contact and replies via R1 using unauthenticated Sealed Sender carrying Bob's DeliveryToken
        await bob.ApproveContactAsync(alice.IdentityId).ConfigureAwait(false);

        var bobChannelId = ChannelId.DeterministicForDirect(bob.IdentityId, alice.IdentityId, relay.Endpoint);
        await bob.SendProfileCapabilityAsync(bobChannelId, alice.IdentityId).ConfigureAwait(false);

        // 7. Alice receives Bob's reply via R1, transitions channel and contact to Active, and persists Bob's DeliveryToken
        await alice.WaitForChannelStatusAsync(aliceChannelId, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await bob.WaitForChannelStatusAsync(bobChannelId, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var aliceRoutingRepo = alice.Services.GetRequiredService<IPeerMailboxRoutingRepository>();
        var bobRoutingOnAlice = await aliceRoutingRepo.GetRouteForExchangeAsync(
            bob.IdentityId,
            relay.Endpoint,
            DateTimeOffset.UtcNow).ConfigureAwait(false);

        bobRoutingOnAlice.Should().NotBeNull();
        bobRoutingOnAlice!.DeliveryToken.Should().NotBeNull();

        // 8. Symmetric Ratchet & Unauthenticated Sealed Sender 4-message exchange
        // Message 1: Alice -> Bob
        const string msg1 = "Message 1 from Alice";
        await alice.SendChatAsync(aliceChannelId, msg1).ConfigureAwait(false);
        var received1 = await bob.WaitForMessageAsync(bobChannelId, msg1, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        received1.Plaintext.Should().Be(msg1);
        received1.AuthorId.Should().Be(alice.IdentityId);

        // Message 2: Bob -> Alice
        const string msg2 = "Message 2 from Bob";
        await bob.SendChatAsync(bobChannelId, msg2).ConfigureAwait(false);
        var received2 = await alice.WaitForMessageAsync(aliceChannelId, msg2, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        received2.Plaintext.Should().Be(msg2);
        received2.AuthorId.Should().Be(bob.IdentityId);

        // Message 3: Alice -> Bob
        const string msg3 = "Message 3 from Alice";
        await alice.SendChatAsync(aliceChannelId, msg3).ConfigureAwait(false);
        var received3 = await bob.WaitForMessageAsync(bobChannelId, msg3, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        received3.Plaintext.Should().Be(msg3);
        received3.AuthorId.Should().Be(alice.IdentityId);

        // Message 4: Bob -> Alice
        const string msg4 = "Message 4 from Bob";
        await bob.SendChatAsync(bobChannelId, msg4).ConfigureAwait(false);
        var received4 = await alice.WaitForMessageAsync(aliceChannelId, msg4, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        received4.Plaintext.Should().Be(msg4);
        received4.AuthorId.Should().Be(bob.IdentityId);

        // Verify Double Ratchet session advanced
        var aliceSessionRepo = alice.Services.GetRequiredService<IRatchetSessionRepository>();
        var aliceSession = await aliceSessionRepo.GetSessionAsync(
            alice.IdentityId,
            bob.IdentityId,
            bob.DeviceId).ConfigureAwait(false);

        aliceSession.Should().NotBeNull();
        aliceSession!.PreviousSendingChainLength.Should().BeGreaterThan(0);
        aliceSession.ReceivingCounter.Should().BeGreaterThan(0);
        aliceSession.RemoteEphemeralPublicKey.Should().NotBeNull();

        var bobSessionRepo = bob.Services.GetRequiredService<IRatchetSessionRepository>();
        var bobSession = await bobSessionRepo.GetSessionAsync(
            bob.IdentityId,
            alice.IdentityId,
            alice.DeviceId).ConfigureAwait(false);

        bobSession.Should().NotBeNull();
        bobSession!.SendingCounter.Should().BeGreaterThan(0);
        bobSession.PreviousSendingChainLength.Should().BeGreaterThan(0);
        bobSession.ReceivingCounter.Should().BeGreaterThan(0);
        bobSession.RemoteEphemeralPublicKey.Should().NotBeNull();
    }
}
