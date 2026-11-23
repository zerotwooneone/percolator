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
using Percolator.Domain.Security.Ports;
using Percolator.PluginSdk.Profiles;
using Microsoft.Extensions.DependencyInjection;

[TestFixture]
public sealed class MultiExchangeSameRelayReplyIntegrationTests
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
    public async Task MultiExchange_Option3A_ChannelAndRatchetIsolation_And_SameRelayReplies()
    {
        // 1. Start Exchange R1 (Friends) and Exchange R2 (Work)
        var r1 = await _topology.CreateRelayAsync().ConfigureAwait(false);
        var r2 = await _topology.CreateRelayAsync().ConfigureAwait(false);

        // 2. Provision Personas Alice and Bob on both R1 and R2
        var alice = await _topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await _topology.CreatePersonaAsync("Bob").ConfigureAwait(false);

        await alice.LinkRelayAsync(r1).ConfigureAwait(false);
        await alice.LinkRelayAsync(r2).ConfigureAwait(false);
        await bob.LinkRelayAsync(r1).ConfigureAwait(false);
        await bob.LinkRelayAsync(r2).ConfigureAwait(false);

        // 3. Bob exports two invitations: one pointing to R1 and one pointing to R2
        var inviteR1 = await bob.CreateInvitationAsync(r1).ConfigureAwait(false);
        var inviteR2 = await bob.CreateInvitationAsync(r2).ConfigureAwait(false);

        // 4. Alice connects to Bob via R1 invitation
        var aliceChannelIdR1 = await alice.ConnectWithInvitationAsync(inviteR1).ConfigureAwait(false);
        var bobContactReqR1 = await bob.WaitForInboundContactRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        bobContactReqR1.OwnerIdentityId.Should().Be(bob.IdentityId);
        bobContactReqR1.RemotePeerId.Should().Be(alice.IdentityId);
        await bob.ApproveContactAsync(alice.IdentityId).ConfigureAwait(false);

        var bobChannelIdR1 = ChannelId.DeterministicForDirect(bob.IdentityId, alice.IdentityId, r1.Endpoint);
        await bob.SendProfileCapabilityAsync(bobChannelIdR1, alice.IdentityId).ConfigureAwait(false);
        await alice.WaitForChannelStatusAsync(aliceChannelIdR1, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await bob.WaitForChannelStatusAsync(bobChannelIdR1, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // 5. Alice connects to Bob via R2 invitation
        var aliceChannelIdR2 = await alice.ConnectWithInvitationAsync(inviteR2).ConfigureAwait(false);
        var bobChannelIdR2 = ChannelId.DeterministicForDirect(bob.IdentityId, alice.IdentityId, r2.Endpoint);
        await bob.WaitForChannelStatusAsync(bobChannelIdR2, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await bob.SendProfileCapabilityAsync(bobChannelIdR2, alice.IdentityId).ConfigureAwait(false);
        await alice.WaitForChannelStatusAsync(aliceChannelIdR2, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // 6. Assert Option 3A isolation:
        // Different channel IDs
        aliceChannelIdR1.Should().NotBe(aliceChannelIdR2);
        bobChannelIdR1.Should().NotBe(bobChannelIdR2);

        // 7. Message exchange on R1
        const string r1MsgAlice = "Hello on Friends R1 from Alice";
        await alice.SendChatAsync(aliceChannelIdR1, r1MsgAlice).ConfigureAwait(false);
        var r1ReceivedBob = await bob.WaitForMessageAsync(bobChannelIdR1, r1MsgAlice, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        r1ReceivedBob.Plaintext.Should().Be(r1MsgAlice);

        const string r1MsgBob = "Reply on Friends R1 from Bob";
        await bob.SendChatAsync(bobChannelIdR1, r1MsgBob).ConfigureAwait(false);
        var r1ReceivedAlice = await alice.WaitForMessageAsync(aliceChannelIdR1, r1MsgBob, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        r1ReceivedAlice.Plaintext.Should().Be(r1MsgBob);

        // 8. Message exchange on R2
        const string r2MsgAlice = "Hello on Work R2 from Alice";
        await alice.SendChatAsync(aliceChannelIdR2, r2MsgAlice).ConfigureAwait(false);
        var r2ReceivedBob = await bob.WaitForMessageAsync(bobChannelIdR2, r2MsgAlice, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        r2ReceivedBob.Plaintext.Should().Be(r2MsgAlice);

        const string r2MsgBob = "Reply on Work R2 from Bob";
        await bob.SendChatAsync(bobChannelIdR2, r2MsgBob).ConfigureAwait(false);
        var r2ReceivedAlice = await alice.WaitForMessageAsync(aliceChannelIdR2, r2MsgBob, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        r2ReceivedAlice.Plaintext.Should().Be(r2MsgBob);

        // 9. Verify independent ratchets and reply strictly routed to originating exchange
        var aliceRoutingRepo = alice.Services.GetRequiredService<IPeerMailboxRoutingRepository>();
        var bobRouteR1 = await aliceRoutingRepo.GetRouteForExchangeAsync(bob.IdentityId, r1.Endpoint, DateTimeOffset.UtcNow).ConfigureAwait(false);
        var bobRouteR2 = await aliceRoutingRepo.GetRouteForExchangeAsync(bob.IdentityId, r2.Endpoint, DateTimeOffset.UtcNow).ConfigureAwait(false);

        bobRouteR1.Should().NotBeNull();
        bobRouteR2.Should().NotBeNull();
        bobRouteR1!.RelayEndpoint.Should().Be(r1.Endpoint);
        bobRouteR2!.RelayEndpoint.Should().Be(r2.Endpoint);
    }
}
