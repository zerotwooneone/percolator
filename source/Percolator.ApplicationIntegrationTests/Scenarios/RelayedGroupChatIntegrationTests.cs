namespace Percolator.ApplicationIntegrationTests.Scenarios;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.ApplicationIntegrationTests.Harness;
using Percolator.Domain.Channels.ValueObjects;

[TestFixture]
public sealed class RelayedGroupChatIntegrationTests
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
    public async Task RelayedGroupChat_SymmetricMessaging_And_EpochRotationEviction()
    {
        // 1. Start Relay R1
        var relay = await _topology.CreateRelayAsync().ConfigureAwait(false);

        // 2. Provision Alice, Bob, and Carol on Relay R1
        var alice = await _topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await _topology.CreatePersonaAsync("Bob").ConfigureAwait(false);
        var carol = await _topology.CreatePersonaAsync("Carol").ConfigureAwait(false);

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await carol.LinkRelayAsync(relay).ConfigureAwait(false);

        // 3. Pairwise 1:1 sessions established between all pairs
        await _topology.PairPersonasAsync(bob, alice, relay).ConfigureAwait(false);
        await _topology.PairPersonasAsync(carol, alice, relay).ConfigureAwait(false);
        await _topology.PairPersonasAsync(carol, bob, relay).ConfigureAwait(false);

        // 4. Alice creates Group V2, generates Sender Key, distributes keys to Bob and Carol via pairwise RelayedOneToOne
        var allPersonas = new List<TestPersonaNode> { alice, bob, carol };
        var allMemberIds = allPersonas.Select(p => p.IdentityId).ToList();

        var channelId = await alice.CreateGroupAsync(relay, "Dev Team", allPersonas).ConfigureAwait(false);

        // Wait for Bob and Carol to receive Alice's Sender Key distribution over pairwise RelayedOneToOne
        await bob.WaitForGroupReceiverSessionAsync(channelId, alice.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await carol.WaitForGroupReceiverSessionAsync(channelId, alice.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // 5. Bob and Carol join, generate Sender Keys, distribute via RelayedOneToOne
        await bob.JoinGroupAsync(relay, channelId, "Dev Team", alice.IdentityId, allMemberIds).ConfigureAwait(false);
        await carol.JoinGroupAsync(relay, channelId, "Dev Team", alice.IdentityId, allMemberIds).ConfigureAwait(false);

        // Wait for all pairwise Sender Key distributions to settle
        await alice.WaitForGroupReceiverSessionAsync(channelId, bob.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await alice.WaitForGroupReceiverSessionAsync(channelId, carol.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        await bob.WaitForGroupReceiverSessionAsync(channelId, carol.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await carol.WaitForGroupReceiverSessionAsync(channelId, bob.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // 6. Symmetric Group Messaging:
        // Alice sends group message -> Bob and Carol both decrypt with Alice's Sender Key
        await alice.SendChatAsync(channelId, "Hello from Alice!").ConfigureAwait(false);
        var bobRecv1 = await bob.WaitForMessageAsync(channelId, "Hello from Alice!", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var carolRecv1 = await carol.WaitForMessageAsync(channelId, "Hello from Alice!", TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        bobRecv1.AuthorId.Should().Be(alice.IdentityId);
        bobRecv1.Plaintext.Should().Be("Hello from Alice!");
        carolRecv1.AuthorId.Should().Be(alice.IdentityId);
        carolRecv1.Plaintext.Should().Be("Hello from Alice!");

        // Bob sends group message -> Alice and Carol both decrypt with Bob's Sender Key
        await bob.SendChatAsync(channelId, "Hey Alice, Bob here.").ConfigureAwait(false);
        var aliceRecv1 = await alice.WaitForMessageAsync(channelId, "Hey Alice, Bob here.", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var carolRecv2 = await carol.WaitForMessageAsync(channelId, "Hey Alice, Bob here.", TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        aliceRecv1.AuthorId.Should().Be(bob.IdentityId);
        aliceRecv1.Plaintext.Should().Be("Hey Alice, Bob here.");
        carolRecv2.AuthorId.Should().Be(bob.IdentityId);
        carolRecv2.Plaintext.Should().Be("Hey Alice, Bob here.");

        // Carol sends group message -> Alice and Bob both decrypt with Carol's Sender Key
        await carol.SendChatAsync(channelId, "Carol joining the discussion.").ConfigureAwait(false);
        var aliceRecv2 = await alice.WaitForMessageAsync(channelId, "Carol joining the discussion.", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var bobRecv2 = await bob.WaitForMessageAsync(channelId, "Carol joining the discussion.", TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        aliceRecv2.AuthorId.Should().Be(carol.IdentityId);
        aliceRecv2.Plaintext.Should().Be("Carol joining the discussion.");
        bobRecv2.AuthorId.Should().Be(carol.IdentityId);
        bobRecv2.Plaintext.Should().Be("Carol joining the discussion.");

        // 7. Epoch Rotation Gate:
        // Alice removes Carol; Alice rotates group epoch and distributes new Sender Key to Bob only
        await alice.EvictGroupMemberAsync(relay, channelId, carol, [alice, bob]).ConfigureAwait(false);

        // Wait deterministically for Alice's rotated key distribution to reach Bob
        await bob.WaitForGroupReceiverSessionAsync(channelId, alice.IdentityId, TimeSpan.FromSeconds(10), minKeyId: 2).ConfigureAwait(false);

        // Alice sends message -> Bob decrypts successfully; Carol receives envelope but cannot decrypt (ratchet rejection)
        await alice.SendChatAsync(channelId, "Post-eviction message for Bob only.").ConfigureAwait(false);

        var bobRecv3 = await bob.WaitForMessageAsync(channelId, "Post-eviction message for Bob only.", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        bobRecv3.AuthorId.Should().Be(alice.IdentityId);
        bobRecv3.Plaintext.Should().Be("Post-eviction message for Bob only.");

        // Carol must NOT receive the post-eviction message
        var carolCheck = async () => await carol.WaitForMessageAsync(channelId, "Post-eviction message for Bob only.", TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await carolCheck.Should().ThrowAsync<TimeoutException>("Carol was evicted and must not decrypt post-eviction messages");
    }
}
