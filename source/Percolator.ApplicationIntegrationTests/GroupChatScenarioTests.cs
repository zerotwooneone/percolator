using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2IntegrationTests.Builders;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using DeliveryRoute = Percolator.PluginSdk.DeliveryRoute;

namespace Percolator.Application2IntegrationTests;

[TestFixture]
public sealed class GroupChatScenarioTests
{
    [Test]
    public async Task Phase3_GroupChat_Alice_Bob_Charlie_HostedOnRelay()
    {
        // ----------------------------------------------------------------------------------
        // Setup: Build cluster with Alice (15001), Bob (15002), Charlie (15004), and Relay (15003)
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

        // Register mailboxes on Relay
        alice.RegisterMailbox(relay);
        bob.RegisterMailbox(relay);
        charlie.RegisterMailbox(relay);

        // Configure routing paths for relayed communication
        var relayEndpoint = RelayEndpoint.FromHostAndPort("localhost", relay.Port.Value);
        bob.AddPeerMailboxRoute(charlie.IdentityId, relayEndpoint, charlie.MailboxRoutingToken, charlie.MailboxDeliveryToken);
        charlie.AddPeerMailboxRoute(bob.IdentityId, relayEndpoint, bob.MailboxRoutingToken, bob.MailboxDeliveryToken);

        // 1. Establish pairwise 1:1 sessions
        // Alice <-> Bob via direct stream
        var aliceBobChannelId = await alice.EstablishDirectSessionWithAsync(bob);
        cluster.Switch.HasActiveStream(alice.Port, bob.Port).Should().BeTrue();

        // Alice <-> Charlie via RelayedOneToOne session (simulated offline prekey & Base64 invite handshake)
        var aliceCharlieChannelId = await alice.EstablishRelayedSessionWithAsync(charlie, relay);

        // ----------------------------------------------------------------------------------
        // Step 1: Group Creation & Relay Ledger Registration
        // ----------------------------------------------------------------------------------
        var groupChannelId = ChannelId.New();
        const string groupName = "Percolator Builders";

        // Alice creates GroupChannel aggregate
        var genesisGroupResult = GroupChannel.CreateGenesis(groupChannelId, alice.IdentityId, groupName, alice.TimeProvider);
        genesisGroupResult.IsSuccess.Should().BeTrue();
        var groupChannel = genesisGroupResult.Value!;
        groupChannel.AddMember(alice.IdentityId, bob.IdentityId, ChannelRole.Member, alice.TimeProvider);
        groupChannel.AddMember(alice.IdentityId, charlie.IdentityId, ChannelRole.Member, alice.TimeProvider);

        await alice.SaveGroupChannelInternalAsync(groupChannel);

        // Generate Group Master Key and Auth Credential MAC
        var masterKeyBytes = new byte[32];
        RandomNumberGenerator.Fill(masterKeyBytes);
        using var groupMasterKey = GroupMasterKey.FromSpan(masterKeyBytes);

        var authMacBytes = new byte[32];
        RandomNumberGenerator.Fill(authMacBytes);
        var authMac = AuthCredentialMacBytes.FromSpan(authMacBytes);

        // Register group credentials on network switch for test transport dispatch
        cluster.Switch.RegisterGroupCredentials(groupChannelId, groupMasterKey, authMac);
        cluster.Switch.RegisterRelayForChannel(groupChannelId, relay);

        // Create and register RelayGroupLedger on Relay
        var activeTokens = new HashSet<BlindedRoutingToken>
        {
            alice.MailboxRoutingToken,
            bob.MailboxRoutingToken,
            charlie.MailboxRoutingToken
        };
        var publicParams = ZkGroupPublicParams.FromBytesOwned(new byte[64]);
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[128]);

        var ledgerResult = RelayGroupLedger.CreateGenesis(
            groupChannelId,
            relay.RelayIdentityId,
            genesisBlob,
            activeTokens,
            publicParams,
            alice.TimeProvider);

        ledgerResult.IsSuccess.Should().BeTrue();
        var ledger = ledgerResult.Value!;
        relay.RegisterGroupLedger(ledger);

        // ----------------------------------------------------------------------------------
        // Step 2: Pairwise Invitation & Acceptance
        // Alice invites Bob over DirectP2P, Charlie over RelayedOneToOne
        // ----------------------------------------------------------------------------------
        ulong expiryMs = (ulong)alice.TimeProvider.UtcNow.AddDays(7).ToUnixTimeMilliseconds();

        // 1. Alice invites Bob (DirectP2P)
        var bobInvitationId = InvitationId.New();
        var bobInviteDto = new GroupInvitationDto(
            bobInvitationId,
            groupChannelId,
            groupName,
            alice.IdentityId,
            $"localhost:{relay.Port.Value}",
            expiryMs);

        var bobInviteResult = await alice.GroupInvitationService.SendInvitationAsync(
            bobInviteDto,
            aliceBobChannelId,
            bob.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.DirectP2P);

        bobInviteResult.IsSuccess.Should().BeTrue();
        var aliceJobs = await alice.OutboxQueries.GetJobsForOwnerAsync(alice.IdentityId);
        aliceJobs.Should().Contain(j => j.RecipientIdentityId == bob.IdentityId && j.Status == OutboxStatus.Delivered);

        // 2. Alice invites Charlie (RelayedOneToOne)
        var charlieInvitationId = InvitationId.New();
        var charlieInviteDto = new GroupInvitationDto(
            charlieInvitationId,
            groupChannelId,
            groupName,
            alice.IdentityId,
            $"localhost:{relay.Port.Value}",
            expiryMs);

        var charlieInviteResult = await alice.GroupInvitationService.SendInvitationAsync(
            charlieInviteDto,
            aliceCharlieChannelId,
            charlie.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.RelayedOneToOne);

        charlieInviteResult.IsSuccess.Should().BeTrue();

        // Alice outbox dispatches to Relay for Charlie
        await alice.OutboxWorker.ProcessBatchAsync();
        relay.MailboxQueue.TotalCount.Should().Be(1);

        // Charlie drains mailbox and receives invitation
        await charlie.DrainMailboxAsync(relay);
        relay.MailboxQueue.TotalCount.Should().Be(0);

        // Strict CQRS: Verify Bob and Charlie see their invitations
        var bobInvitations = await bob.GroupInvitationQueries.GetPendingInvitationsAsync();
        bobInvitations.Should().ContainSingle(i => i.InvitationId == bobInvitationId && i.GroupName == groupName);

        var charlieInvitations = await charlie.GroupInvitationQueries.GetPendingInvitationsAsync();
        charlieInvitations.Should().ContainSingle(i => i.InvitationId == charlieInvitationId && i.GroupName == groupName);

        // Bob accepts invitation
        var bobAcceptResult = await bob.GroupInvitationService.AcceptInvitationAsync(
            bobInvitationId,
            bob.IdentityId,
            DeviceId.Primary,
            aliceBobChannelId,
            DeliveryRoute.DirectP2P);

        bobAcceptResult.IsSuccess.Should().BeTrue();

        // Charlie accepts invitation
        var charlieAcceptResult = await charlie.GroupInvitationService.AcceptInvitationAsync(
            charlieInvitationId,
            charlie.IdentityId,
            DeviceId.Primary,
            aliceCharlieChannelId,
            DeliveryRoute.RelayedOneToOne);

        charlieAcceptResult.IsSuccess.Should().BeTrue();

        // Charlie flushes response to Relay; Alice drains
        await charlie.OutboxWorker.ProcessBatchAsync();
        await alice.DrainMailboxAsync(relay);

        // ----------------------------------------------------------------------------------
        // Step 3: Pairwise Sender Key Distribution
        // Alice distributes her sender key to Bob and Charlie
        // ----------------------------------------------------------------------------------
        var distResult = await alice.GroupKeyDistributionService.DistributeSenderKeyAsync(
            groupChannelId,
            alice.IdentityId,
            DeviceId.Primary,
            [bob.IdentityId, charlie.IdentityId]);

        distResult.IsSuccess.Should().BeTrue();

        // Alice outbox flushes the relayed sender key distribution message to Charlie
        await alice.OutboxWorker.ProcessBatchAsync();
        relay.MailboxQueue.TotalCount.Should().Be(1);

        // Charlie drains mailbox and imports Alice's sender key
        await charlie.DrainMailboxAsync(relay);
        relay.MailboxQueue.TotalCount.Should().Be(0);

        // ----------------------------------------------------------------------------------
        // Step 4: Zero-Knowledge Relayed Broadcast
        // Alice broadcasts to the group via DeliveryRoute.RelayedGroup
        // ----------------------------------------------------------------------------------
        cluster.AdvanceTime(TimeSpan.FromSeconds(1));
        ulong broadcastTimeMs = (ulong)alice.TimeProvider.UtcNow.ToUnixTimeMilliseconds();

        const string broadcastMessage = "Welcome to the group, Bob and Charlie!";
        var sendBroadcastResult = await alice.ChatService.SendMessageAsync(new SendMessageCommand(
            groupChannelId,
            alice.IdentityId,
            null, // Group broadcast has no single 1:1 recipient
            DeviceId.Primary,
            DeliveryRoute.RelayedGroup,
            broadcastMessage));

        sendBroadcastResult.IsSuccess.Should().BeTrue();
        var broadcastPayloadId = sendBroadcastResult.Value;

        // Alice outbox dispatches broadcast to Relay
        var processedBroadcastJobs = await alice.OutboxWorker.ProcessBatchAsync();
        processedBroadcastJobs.Should().Be(1);

        // Assert Relay verified ZK presentation proof and fanned out envelopes to active members
        relay.MailboxQueue.TotalCount.Should().BeGreaterThan(0, "Relay must fan out group broadcast to registered routing tokens");

        // Bob and Charlie drain their mailboxes
        var bobDrained = await bob.DrainMailboxAsync(relay);
        bobDrained.Should().BeGreaterThan(0);

        var charlieDrained = await charlie.DrainMailboxAsync(relay);
        charlieDrained.Should().BeGreaterThan(0);

        // ----------------------------------------------------------------------------------
        // Assert: Strict CQRS Queries on Bob and Charlie
        // ----------------------------------------------------------------------------------
        // 1. Bob asserts group message content, author, and conversation summary
        var bobGroupMessages = await bob.ChatQueries.GetMessagesAsync(groupChannelId, null, 50, broadcastTimeMs);
        bobGroupMessages.Should().ContainSingle();
        bobGroupMessages[0].Id.Should().Be(broadcastPayloadId);
        bobGroupMessages[0].AuthorId.Should().Be(alice.IdentityId);
        bobGroupMessages[0].Content.Should().Be(broadcastMessage);
        bobGroupMessages[0].ChannelId.Should().Be(groupChannelId);

        var bobConversations = await bob.ConversationQueries.GetConversationsAsync(bob.IdentityId, broadcastTimeMs);
        bobConversations.Should().ContainSingle(c => c.ChannelId == groupChannelId);
        var bobGroupConv = bobConversations.Single(c => c.ChannelId == groupChannelId);
        bobGroupConv.UnreadCount.Should().Be(1);
        bobGroupConv.LastAuthorId.Should().Be(alice.IdentityId);
        bobGroupConv.LastMessageSnippet.Should().Be(broadcastMessage);

        // 2. Charlie asserts group message content, author, and conversation summary
        var charlieGroupMessages = await charlie.ChatQueries.GetMessagesAsync(groupChannelId, null, 50, broadcastTimeMs);
        charlieGroupMessages.Should().ContainSingle();
        charlieGroupMessages[0].Id.Should().Be(broadcastPayloadId);
        charlieGroupMessages[0].AuthorId.Should().Be(alice.IdentityId);
        charlieGroupMessages[0].Content.Should().Be(broadcastMessage);
        charlieGroupMessages[0].ChannelId.Should().Be(groupChannelId);

        var charlieConversations = await charlie.ConversationQueries.GetConversationsAsync(charlie.IdentityId, broadcastTimeMs);
        charlieConversations.Should().ContainSingle(c => c.ChannelId == groupChannelId);
        var charlieGroupConv = charlieConversations.Single(c => c.ChannelId == groupChannelId);
        charlieGroupConv.UnreadCount.Should().Be(1);
        charlieGroupConv.LastAuthorId.Should().Be(alice.IdentityId);
        charlieGroupConv.LastMessageSnippet.Should().Be(broadcastMessage);
    }
}
