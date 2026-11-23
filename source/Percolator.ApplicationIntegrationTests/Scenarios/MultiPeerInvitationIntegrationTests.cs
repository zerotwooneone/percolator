namespace Percolator.ApplicationIntegrationTests.Scenarios;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Percolator.Application.Connections;
using Percolator.Application.Invitations;
using Percolator.Application.Ports;
using Percolator.ApplicationIntegrationTests.Harness;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk.Profiles;

public sealed class MultiPeerInvitationIntegrationTests
{
    [Test]
    public async Task ConcurrentConnections_SingleInvitation_ConsumesDistinctOneTimePreKeys_And_MaintainsIndependentRatchets()
    {
        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(RelayHostingPolicy.Default).ConfigureAwait(false);

        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);
        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var carol = await topology.CreatePersonaAsync("Carol").ConfigureAwait(false);

        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await carol.LinkRelayAsync(relay).ConfigureAwait(false);

        var bobRoutingToken = await bob.GetRoutingTokenAsync(relay).ConfigureAwait(false);
        int initialOpkCount = await relay.GetAvailableOneTimePreKeyCountAsync(bobRoutingToken).ConfigureAwait(false);
        initialOpkCount.Should().BeGreaterThan(2);

        // Bob exports a single invitation link
        string bobInviteUri = await bob.CreateInvitationAsync(relay).ConfigureAwait(false);

        // Alice and Carol concurrently connect using the exact same invitation link
        var aliceConnectTask = alice.ConnectWithInvitationAsync(bobInviteUri);
        var carolConnectTask = carol.ConnectWithInvitationAsync(bobInviteUri);

        await Task.WhenAll(aliceConnectTask, carolConnectTask).ConfigureAwait(false);
        var aliceChannelId = await aliceConnectTask;
        var carolChannelId = await carolConnectTask;

        // Exactly 2 distinct OTPKs consumed atomically from RelayOneTimePreKeys
        int afterOpkCount = await relay.GetAvailableOneTimePreKeyCountAsync(bobRoutingToken).ConfigureAwait(false);
        afterOpkCount.Should().Be(initialOpkCount - 2);

        // Bob drains contact requests from both Alice and Carol
        await bob.WaitForInboundContactRequestAsync(TimeSpan.FromSeconds(10), alice.IdentityId).ConfigureAwait(false);
        await bob.ApproveContactAsync(alice.IdentityId).ConfigureAwait(false);
        await bob.WaitForInboundContactRequestAsync(TimeSpan.FromSeconds(10), carol.IdentityId).ConfigureAwait(false);
        await bob.ApproveContactAsync(carol.IdentityId).ConfigureAwait(false);

        // Bob delivers delivery tokens
        await bob.SendProfileCapabilityAsync(aliceChannelId, alice.IdentityId).ConfigureAwait(false);
        var bobCarolChannelId = ChannelId.DeterministicForDirect(bob.IdentityId, carol.IdentityId, relay.Endpoint);
        await bob.SendProfileCapabilityAsync(bobCarolChannelId, carol.IdentityId).ConfigureAwait(false);

        await alice.WaitForChannelStatusAsync(aliceChannelId, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await carol.WaitForChannelStatusAsync(carolChannelId, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Independent 4-DH Double Ratchet messaging: zero cross-talk
        await alice.SendChatAsync(aliceChannelId, "Hello Bob from Alice").ConfigureAwait(false);
        await carol.SendChatAsync(carolChannelId, "Hello Bob from Carol").ConfigureAwait(false);

        var bobRecvAlice = await bob.WaitForMessageAsync(aliceChannelId, "Hello Bob from Alice", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        bobRecvAlice.AuthorId.Should().Be(alice.IdentityId);

        var bobRecvCarol = await bob.WaitForMessageAsync(bobCarolChannelId, "Hello Bob from Carol", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        bobRecvCarol.AuthorId.Should().Be(carol.IdentityId);

        // Bob replies to Alice and Carol independently
        await bob.SendChatAsync(aliceChannelId, "Reply to Alice").ConfigureAwait(false);
        await bob.SendChatAsync(bobCarolChannelId, "Reply to Carol").ConfigureAwait(false);

        var aliceRecv = await alice.WaitForMessageAsync(aliceChannelId, "Reply to Alice", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        aliceRecv.Plaintext.Should().Be("Reply to Alice");

        var carolRecv = await carol.WaitForMessageAsync(carolChannelId, "Reply to Carol", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        carolRecv.Plaintext.Should().Be("Reply to Carol");
    }

    [Test]
    public async Task SignedPreKeyRotation_Resilience_DaveConnectsWithOriginalInvitation()
    {
        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(RelayHostingPolicy.Default).ConfigureAwait(false);

        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);
        var dave = await topology.CreatePersonaAsync("Dave").ConfigureAwait(false);

        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await dave.LinkRelayAsync(relay).ConfigureAwait(false);

        // Bob exports original invitation
        string bobOriginalInviteUri = await bob.CreateInvitationAsync(relay).ConfigureAwait(false);

        // Bob rotates his Signed PreKey and publishes new SPK to the relay
        await bob.RotateSignedPreKeyAsync(relay).ConfigureAwait(false);

        // Dave connects using Bob's original invitation URI
        var daveChannelId = await dave.ConnectWithInvitationAsync(bobOriginalInviteUri).ConfigureAwait(false);

        // Bob drains contact request, approves, and exchanges capabilities
        await bob.WaitForInboundContactRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await bob.ApproveContactAsync(dave.IdentityId).ConfigureAwait(false);
        await bob.SendProfileCapabilityAsync(daveChannelId, dave.IdentityId).ConfigureAwait(false);

        await dave.WaitForChannelStatusAsync(daveChannelId, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Verification of symmetric messaging after SPK rotation
        await dave.SendChatAsync(daveChannelId, "Post-rotation handshake message from Dave").ConfigureAwait(false);
        var bobRecv = await bob.WaitForMessageAsync(daveChannelId, "Post-rotation handshake message from Dave", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        bobRecv.Plaintext.Should().Be("Post-rotation handshake message from Dave");
    }

    [Test]
    public async Task MitM_KeySwap_AbortsWithIdentityKeyMismatch_And_ZeroDatabaseMutations()
    {
        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(RelayHostingPolicy.Default).ConfigureAwait(false);

        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);
        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);

        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await alice.LinkRelayAsync(relay).ConfigureAwait(false);

        var bobRoutingToken = await bob.GetRoutingTokenAsync(relay).ConfigureAwait(false);

        // Malicious/tampered invitation: pinned IdentityKey is different from Bob's real key on the relay
        var cryptoEngine = alice.Services.GetRequiredService<ICryptoEngine>();
        var (_, fakePub) = cryptoEngine.GenerateEphemeralKeyPair();
        var tamperedIdentityKey = IdentityKey.FromSpan(fakePub.Span);

        var tamperedPayload = new PeerInvitationPayload(
            bob.IdentityId,
            tamperedIdentityKey,
            relay.Endpoint,
            bobRoutingToken);

        // Alice attempts to connect with tampered invitation
        var connectResult = await alice.TryConnectWithInvitationPayloadAsync(tamperedPayload, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        connectResult.IsFailure.Should().BeTrue();
        connectResult.Error.Code.Should().Be("IDENTITY_KEY_MISMATCH");

        // Verify ZERO database mutations on Alice
        var contactRepo = alice.Services.GetRequiredService<IPeerContactRepository>();
        var contact = await contactRepo.GetByPeerIdAsync(alice.IdentityId, bob.IdentityId).ConfigureAwait(false);
        contact.Should().BeNull();

        var channelRepo = alice.Services.GetRequiredService<IChannelRepository>();
        var directChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, bob.IdentityId, relay.Endpoint);
        var channel = await channelRepo.GetDirectByIdAsync(directChannelId).ConfigureAwait(false);
        channel.Should().BeNull();

        var routingRepo = alice.Services.GetRequiredService<IPeerMailboxRoutingRepository>();
        var routing = await routingRepo.GetRouteForExchangeAsync(bob.IdentityId, relay.Endpoint, DateTimeOffset.UtcNow).ConfigureAwait(false);
        routing.Should().BeNull();
    }

    [Test]
    public async Task ReInvitation_KeyChangeGuard_AbortsWithIdentityKeyChanged_And_ZeroDatabaseMutations()
    {
        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(RelayHostingPolicy.Default).ConfigureAwait(false);

        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);
        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);

        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await alice.LinkRelayAsync(relay).ConfigureAwait(false);

        // 1. Initial pairing succeeds
        await topology.PairPersonasAsync(bob, alice, relay).ConfigureAwait(false);

        var contactRepo = alice.Services.GetRequiredService<IPeerContactRepository>();
        var originalContact = await contactRepo.GetByPeerIdAsync(alice.IdentityId, bob.IdentityId).ConfigureAwait(false);
        originalContact.Should().NotBeNull();
        var originalBobKey = originalContact!.PrimaryPublicKey;
        originalBobKey.Should().NotBeNull();

        // 2. Attacker creates re-invitation with changed IdentityKey for existing contact Bob
        var cryptoEngine = alice.Services.GetRequiredService<ICryptoEngine>();
        var (_, fakePub) = cryptoEngine.GenerateEphemeralKeyPair();
        var spoofedIdentityKey = IdentityKey.FromSpan(fakePub.Span);
        var bobRoutingToken = await bob.GetRoutingTokenAsync(relay).ConfigureAwait(false);

        var spoofedPayload = new PeerInvitationPayload(
            bob.IdentityId,
            spoofedIdentityKey,
            relay.Endpoint,
            bobRoutingToken);

        // 3. Alice attempts to re-import spoofed invitation
        var reimportResult = await alice.TryConnectWithInvitationPayloadAsync(spoofedPayload, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        reimportResult.IsFailure.Should().BeTrue();
        reimportResult.Error.Code.Should().Be("IDENTITY_KEY_CHANGED");

        // Verify ZERO database mutation on Alice: Bob's pinned key is unchanged
        var refreshedContact = await contactRepo.GetByPeerIdAsync(alice.IdentityId, bob.IdentityId).ConfigureAwait(false);
        refreshedContact.Should().NotBeNull();
        refreshedContact!.PrimaryPublicKey!.Span.ToArray().Should().Equal(originalBobKey!.Span.ToArray());
    }
}
