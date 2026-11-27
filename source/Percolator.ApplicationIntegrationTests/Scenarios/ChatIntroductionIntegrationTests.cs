namespace Percolator.ApplicationIntegrationTests.Scenarios;

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.ApplicationIntegrationTests.Harness;
using Percolator.Apps.Chat.Features.Introductions;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Hosting;

[TestFixture]
public sealed class ChatIntroductionIntegrationTests
{
    [Test]
    public async Task ChatIntroduction_AskBeforeIntroducing_Allowed_ConnectsAndExchangesBidirectionalDoubleRatchetMessages()
    {
        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(RelayHostingPolicy.Default).ConfigureAwait(false);

        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);
        var charlie = await topology.CreatePersonaAsync("Charlie").ConfigureAwait(false);

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await charlie.LinkRelayAsync(relay).ConfigureAwait(false);

        // Precondition: Alice is paired with Bob, and Alice is paired with Charlie.
        // Bob and Charlie have never met and have zero channels or contacts for each other.
        await topology.PairPersonasAsync(alice, bob, relay).ConfigureAwait(false);
        await topology.PairPersonasAsync(alice, charlie, relay).ConfigureAwait(false);

        var aliceBobChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, bob.IdentityId, relay.Endpoint);
        var aliceCharlieChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, charlie.IdentityId, relay.Endpoint);

        // 1. Consent Ask (Alice -> Bob over existing 1:1 Double Ratchet)
        // Alice asks Bob to introduce Charlie, specifying forwardChannelId for auto-forwarding upon approval
        var requestId = await alice.RequestIntroductionConsentAsync(
            aliceBobChannelId,
            candidatePeerId: charlie.IdentityId,
            candidateDisplayName: "Charlie",
            forwardChannelId: aliceCharlieChannelId,
            note: "Bob, meet Charlie!").ConfigureAwait(false);

        // 2. Per-Introduction Consent (Bob)
        // Bob receives the consent request over the Double Ratchet channel
        var bobRequest = await bob.WaitForInboundIntroductionConsentRequestAsync(TimeSpan.FromSeconds(10), alice.IdentityId).ConfigureAwait(false);
        bobRequest.RequestId.Should().Be(requestId);
        bobRequest.CandidatePeerId.Should().Be(charlie.IdentityId);
        bobRequest.CandidateDisplayName.Should().Be("Charlie");
        bobRequest.Note.Should().Be("Bob, meet Charlie!");

        // Bob approves the introduction request, generating Bob's PeerInvitation
        await bob.RespondToIntroductionConsentAsync(bobRequest.RequestId, allow: true).ConfigureAwait(false);

        // 3. E2E Forward (Alice receives Bob's consent and forwards card to Charlie)
        var aliceResponse = await alice.WaitForIntroductionConsentResponseAsync(TimeSpan.FromSeconds(10), requestId).ConfigureAwait(false);
        aliceResponse.Allowed.Should().BeTrue();
        aliceResponse.Invitation.Should().NotBeNull();
        aliceResponse.Invitation!.InviterIdentityId.Should().Be(bob.IdentityId);

        // Charlie receives the PeerIntroductionCard over the Alice-Charlie Double Ratchet channel
        var charlieCard = await charlie.WaitForPeerIntroductionCardAsync(TimeSpan.FromSeconds(10), alice.IdentityId).ConfigureAwait(false);
        charlieCard.RequestId.Should().Be(requestId);
        charlieCard.IntroducedPeerId.Should().Be(bob.IdentityId);
        charlieCard.IntroducedDisplayName.Should().Be("Bob");
        charlieCard.Invitation.Should().NotBeNull();
        charlieCard.Invitation.InviterIdentityId.Should().Be(bob.IdentityId);
        charlieCard.Invitation.RelayEndpoint.Should().Be(relay.Endpoint);

        // 4. TOFU Gate & Connection (Charlie connects to Bob using Bob's invitation)
        await charlie.AcceptPeerIntroductionCardAsync(charlieCard.RequestId).ConfigureAwait(false);

        // Bob receives Charlie's inbound contact request via SYS-CONTACT-CONSENT-GATE
        await bob.WaitForInboundContactRequestAsync(TimeSpan.FromSeconds(10), charlie.IdentityId).ConfigureAwait(false);

        // Bob approves Charlie
        await bob.ApproveContactAsync(charlie.IdentityId).ConfigureAwait(false);

        // Bob sends profile capability / initial reply to deliver delivery token to Charlie
        var bobCharlieChannelId = ChannelId.DeterministicForDirect(bob.IdentityId, charlie.IdentityId, relay.Endpoint);
        await bob.SendProfileCapabilityAsync(bobCharlieChannelId, charlie.IdentityId).ConfigureAwait(false);

        // Both channels transition to Active
        var charlieBobChannelId = ChannelId.DeterministicForDirect(charlie.IdentityId, bob.IdentityId, relay.Endpoint);
        await charlie.WaitForChannelStatusAsync(charlieBobChannelId, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await bob.WaitForChannelStatusAsync(bobCharlieChannelId, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // 5. Bidirectional Double Ratchet Chat between Bob and Charlie
        await charlie.SendChatAsync(charlieBobChannelId, "Hello Bob, nice to meet you via Alice!").ConfigureAwait(false);
        var bobRecv = await bob.WaitForMessageAsync(bobCharlieChannelId, "Hello Bob, nice to meet you via Alice!", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        bobRecv.AuthorId.Should().Be(charlie.IdentityId);

        await bob.SendChatAsync(bobCharlieChannelId, "Hi Charlie, great to connect!").ConfigureAwait(false);
        var charlieRecv = await charlie.WaitForMessageAsync(charlieBobChannelId, "Hi Charlie, great to connect!", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        charlieRecv.AuthorId.Should().Be(bob.IdentityId);
        charlieRecv.Plaintext.Should().Be("Hi Charlie, great to connect!");

        // 6. Verify SQLite Persistence across all three nodes
        var aliceConsents = await alice.GetIntroductionConsentsAsync().ConfigureAwait(false);
        var aliceRecord = aliceConsents.FirstOrDefault(c => c.RequestId == requestId);
        aliceRecord.Should().NotBeNull();
        aliceRecord!.Status.Should().Be(IntroductionStatus.Forwarded);

        var bobConsents = await bob.GetIntroductionConsentsAsync().ConfigureAwait(false);
        var bobRecord = bobConsents.FirstOrDefault(c => c.RequestId == requestId);
        bobRecord.Should().NotBeNull();
        bobRecord!.Status.Should().Be(IntroductionStatus.Allowed);

        var charlieConsents = await charlie.GetIntroductionConsentsAsync().ConfigureAwait(false);
        var charlieRecord = charlieConsents.FirstOrDefault(c => c.RequestId == requestId);
        charlieRecord.Should().NotBeNull();
        charlieRecord!.Status.Should().Be(IntroductionStatus.Connected);
    }

    [Test]
    public async Task ChatIntroduction_DeclinedConsent_ZeroContactCardTransmitted_AndZeroConnectionAttempt()
    {
        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(RelayHostingPolicy.Default).ConfigureAwait(false);

        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);
        var charlie = await topology.CreatePersonaAsync("Charlie").ConfigureAwait(false);

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await charlie.LinkRelayAsync(relay).ConfigureAwait(false);

        await topology.PairPersonasAsync(alice, bob, relay).ConfigureAwait(false);
        await topology.PairPersonasAsync(alice, charlie, relay).ConfigureAwait(false);

        var aliceBobChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, bob.IdentityId, relay.Endpoint);
        var aliceCharlieChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, charlie.IdentityId, relay.Endpoint);

        // Alice asks Bob to introduce Charlie
        var requestId = await alice.RequestIntroductionConsentAsync(
            aliceBobChannelId,
            candidatePeerId: charlie.IdentityId,
            candidateDisplayName: "Charlie",
            forwardChannelId: aliceCharlieChannelId).ConfigureAwait(false);

        // Bob receives request
        var bobRequest = await bob.WaitForInboundIntroductionConsentRequestAsync(TimeSpan.FromSeconds(10), alice.IdentityId).ConfigureAwait(false);

        // Bob declines the introduction request
        await bob.RespondToIntroductionConsentAsync(
            bobRequest.RequestId,
            allow: false,
            declineReason: "Not accepting new contacts right now.").ConfigureAwait(false);

        // Alice receives the declined response
        var aliceResponse = await alice.WaitForIntroductionConsentResponseAsync(TimeSpan.FromSeconds(10), requestId).ConfigureAwait(false);
        aliceResponse.Allowed.Should().BeFalse();
        aliceResponse.DeclineReason.Should().Be("Not accepting new contacts right now.");
        aliceResponse.Invitation.Should().BeNull();

        // Charlie's SQLite introduction repository remains completely clean (zero cards transmitted)
        var charlieConsents = await charlie.GetIntroductionConsentsAsync().ConfigureAwait(false);
        charlieConsents.Should().BeEmpty();

        // Alice's consent repository records the Declined status
        var aliceConsents = await alice.GetIntroductionConsentsAsync().ConfigureAwait(false);
        var aliceRecord = aliceConsents.FirstOrDefault(c => c.RequestId == requestId);
        aliceRecord.Should().NotBeNull();
        aliceRecord!.Status.Should().Be(IntroductionStatus.Declined);
    }

    [Test]
    public async Task ChatIntroduction_ManualForwarding_WhenForwardChannelNotPreset()
    {
        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(RelayHostingPolicy.Default).ConfigureAwait(false);

        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);
        var charlie = await topology.CreatePersonaAsync("Charlie").ConfigureAwait(false);

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await charlie.LinkRelayAsync(relay).ConfigureAwait(false);

        await topology.PairPersonasAsync(alice, bob, relay).ConfigureAwait(false);
        await topology.PairPersonasAsync(alice, charlie, relay).ConfigureAwait(false);

        var aliceBobChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, bob.IdentityId, relay.Endpoint);
        var aliceCharlieChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, charlie.IdentityId, relay.Endpoint);

        // Alice asks Bob to introduce Charlie WITHOUT setting ForwardChannelId (manual review flow)
        var requestId = await alice.RequestIntroductionConsentAsync(
            aliceBobChannelId,
            candidatePeerId: charlie.IdentityId,
            candidateDisplayName: "Charlie",
            forwardChannelId: null).ConfigureAwait(false);

        var bobRequest = await bob.WaitForInboundIntroductionConsentRequestAsync(TimeSpan.FromSeconds(10), alice.IdentityId).ConfigureAwait(false);
        await bob.RespondToIntroductionConsentAsync(bobRequest.RequestId, allow: true).ConfigureAwait(false);

        var aliceResponse = await alice.WaitForIntroductionConsentResponseAsync(TimeSpan.FromSeconds(10), requestId).ConfigureAwait(false);
        aliceResponse.Allowed.Should().BeTrue();

        // Status is Allowed (not yet Forwarded)
        var aliceConsents = await alice.GetIntroductionConsentsAsync().ConfigureAwait(false);
        var consent = aliceConsents.First(c => c.RequestId == requestId);
        consent.Status.Should().Be(IntroductionStatus.Allowed);

        // Alice manually forwards the card to Charlie
        await alice.ForwardIntroductionCardAsync(requestId, aliceCharlieChannelId).ConfigureAwait(false);

        // Charlie receives card and accepts
        var charlieCard = await charlie.WaitForPeerIntroductionCardAsync(TimeSpan.FromSeconds(10), alice.IdentityId).ConfigureAwait(false);
        await charlie.AcceptPeerIntroductionCardAsync(charlieCard.RequestId).ConfigureAwait(false);

        await bob.WaitForInboundContactRequestAsync(TimeSpan.FromSeconds(10), charlie.IdentityId).ConfigureAwait(false);
        await bob.ApproveContactAsync(charlie.IdentityId).ConfigureAwait(false);

        var bobCharlieChannelId = ChannelId.DeterministicForDirect(bob.IdentityId, charlie.IdentityId, relay.Endpoint);
        await bob.SendProfileCapabilityAsync(bobCharlieChannelId, charlie.IdentityId).ConfigureAwait(false);

        var charlieBobChannelId = ChannelId.DeterministicForDirect(charlie.IdentityId, bob.IdentityId, relay.Endpoint);
        await charlie.WaitForChannelStatusAsync(charlieBobChannelId, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Bidirectional message verified
        await charlie.SendChatAsync(charlieBobChannelId, "Hello Bob from manual forward!").ConfigureAwait(false);
        var bobRecv = await bob.WaitForMessageAsync(bobCharlieChannelId, "Hello Bob from manual forward!", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        bobRecv.AuthorId.Should().Be(charlie.IdentityId);
    }
}
