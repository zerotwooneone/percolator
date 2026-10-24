using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2IntegrationTests.Builders;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using DeliveryRoute = Percolator.PluginSdk.DeliveryRoute;

namespace Percolator.Application2IntegrationTests;

[TestFixture]
public sealed class RelayedChatScenarioTests
{
    [Test]
    public async Task Phase2_Alice_And_Charlie_Handshake_And_Chat_Over_Relay()
    {
        // ----------------------------------------------------------------------------------
        // Step 2.1: Relay Provisioning & Charlie Registration
        // Build cluster with Alice (15001), Charlie (15004), and Relay (15003)
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

        // Alice registers mailbox tokens on Relay to receive relayed messages
        alice.RegisterMailbox(relay);

        // Charlie publishes RelayHostedPreKeyBundle with 5 OTPKs to Relay and registers mailbox tokens
        var charlieHostedBundle = charlie.PublishHostedPreKeys(relay, oneTimeKeyCount: 5);
        charlieHostedBundle.AvailableOneTimePreKeyCount.Should().Be(5);
        relay.MailboxQueue.TotalCount.Should().Be(0);

        // ----------------------------------------------------------------------------------
        // Step 2.2: Alice Consumes Charlie's PreKey Bundle from Relay & Initiates Handshake
        // ----------------------------------------------------------------------------------
        var fetchedHostedBundle = await relay.PreKeyRepository.GetAsync(charlie.IdentityId, charlie.DeviceId);
        fetchedHostedBundle.Should().NotBeNull();

        var consumeResult = fetchedHostedBundle!.ConsumeBundle();
        consumeResult.IsSuccess.Should().BeTrue();
        fetchedHostedBundle.AvailableOneTimePreKeyCount.Should().Be(4, "Consuming a bundle should decrement the available OTPKs on the relay");
        var charlieBundle = consumeResult.Value!;

        // Configure routing paths so Alice and Charlie know each other's relay mailbox routes
        alice.AddPeerMailboxRoute(charlie.IdentityId, $"localhost:{relay.Port.Value}", charlie.MailboxRoutingToken, charlie.MailboxDeliveryToken);
        charlie.AddPeerMailboxRoute(alice.IdentityId, $"localhost:{relay.Port.Value}", alice.MailboxRoutingToken, alice.MailboxDeliveryToken);

        // Alice initiates X3DH handshake targeting Charlie
        var handshakeResult = await alice.HandshakeService.InitiateHandshakeAsync(
            alice.IdentityId,
            DeviceId.Primary,
            charlieBundle);

        handshakeResult.IsSuccess.Should().BeTrue();

        // ----------------------------------------------------------------------------------
        // Step 2.3: Relayed Outbox Dispatch to Relay Mailbox Queue
        // ----------------------------------------------------------------------------------
        var processedAliceJobs = await alice.OutboxWorker.ProcessBatchAsync();
        processedAliceJobs.Should().Be(1, "OutboxWorker should dispatch the pending handshake job");

        var pendingAliceJobs = await alice.OutboxQueries.GetPendingJobsAsync();
        pendingAliceJobs.Should().BeEmpty();

        var allAliceJobs = await alice.OutboxQueries.GetJobsForOwnerAsync(alice.IdentityId);
        allAliceJobs.Should().ContainSingle();
        allAliceJobs[0].RecipientIdentityId.Should().Be(charlie.IdentityId);

        // Assert Relay mailbox has the envelope, and the envelope carries NO cleartext sender ID
        relay.MailboxQueue.TotalCount.Should().Be(1);
        var queuedEnvelope = relay.MailboxQueue.Envelopes.Single();
        queuedEnvelope.RecipientToken.Should().Be(charlie.MailboxRoutingToken);

        // ----------------------------------------------------------------------------------
        // Step 2.4: Charlie Drains Mailbox & Receives Inbound Handshake Request
        // ----------------------------------------------------------------------------------
        var charlieDrained = await charlie.DrainMailboxAsync(relay);
        charlieDrained.Should().Be(1);
        relay.MailboxQueue.TotalCount.Should().Be(0);

        // Assert Charlie's ContactQueries (Strict CQRS): Alice is pending approval
        var contactDetail = await charlie.ContactQueries.GetContactDetailAsync(charlie.IdentityId, alice.IdentityId);
        contactDetail.Should().NotBeNull();
        contactDetail!.State.Should().Be(ContactState.PendingApproval);
        contactDetail.IdentityId.Should().Be(alice.IdentityId);

        // ----------------------------------------------------------------------------------
        // Step 2.5: Charlie Approves Contact Request & Completes Handshake
        // ----------------------------------------------------------------------------------
        var approveResult = await charlie.ContactRequestCoordinator.ApproveRequestAsync(
            charlie.IdentityId,
            alice.IdentityId,
            PeerTrustLevel.Verified);

        approveResult.IsSuccess.Should().BeTrue();

        var completeResult = await charlie.HandshakeService.CompletePendingHandshakeAsync(
            charlie.IdentityId,
            alice.IdentityId);

        completeResult.IsSuccess.Should().BeTrue();

        var approvedContact = await charlie.ContactQueries.GetContactDetailAsync(charlie.IdentityId, alice.IdentityId);
        approvedContact!.State.Should().Be(ContactState.Active);
        approvedContact.TrustLevel.Should().Be(PeerTrustLevel.Verified);

        // ----------------------------------------------------------------------------------
        // Step 2.6: Bidirectional 1:1 Double Ratchet Chat over Relay Mailbox
        // ----------------------------------------------------------------------------------
        await alice.AddApprovedContactAsync(charlie);
        var channelId = await alice.EstablishDirectChannelWithAsync(charlie);

        ulong nowMs = (ulong)alice.TimeProvider.UtcNow.ToUnixTimeMilliseconds();

        // 1. Alice sends chat message to Charlie via DeliveryRoute.RelayedOneToOne
        const string aliceMessage = "Hi Charlie, reaching out via Relay!";
        var sendResult = await alice.ChatService.SendMessageAsync(new SendMessageCommand(
            channelId,
            alice.IdentityId,
            charlie.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.RelayedOneToOne,
            aliceMessage));

        sendResult.IsSuccess.Should().BeTrue();
        var alicePayloadId = sendResult.Value;

        // Alice outbox dispatches to Relay
        await alice.OutboxWorker.ProcessBatchAsync();
        relay.MailboxQueue.TotalCount.Should().Be(1);

        // Charlie drains mailbox and receives message
        await charlie.DrainMailboxAsync(relay);
        relay.MailboxQueue.TotalCount.Should().Be(0);

        // Assert Charlie's ChatQueries & ConversationQueries (Strict CQRS)
        var charlieMessages = await charlie.ChatQueries.GetMessagesAsync(channelId, null, 50, nowMs);
        charlieMessages.Should().ContainSingle();
        charlieMessages[0].Id.Should().Be(alicePayloadId);
        charlieMessages[0].AuthorId.Should().Be(alice.IdentityId);
        charlieMessages[0].Content.Should().Be(aliceMessage);

        var charlieConvs = await charlie.ConversationQueries.GetConversationsAsync(charlie.IdentityId, nowMs);
        charlieConvs.Should().ContainSingle();
        charlieConvs[0].UnreadCount.Should().Be(1);
        charlieConvs[0].LastAuthorId.Should().Be(alice.IdentityId);
        charlieConvs[0].LastMessageSnippet.Should().Be(aliceMessage);

        // 2. Charlie replies back to Alice via DeliveryRoute.RelayedOneToOne
        cluster.AdvanceTime(TimeSpan.FromSeconds(2));
        ulong replyNowMs = (ulong)charlie.TimeProvider.UtcNow.ToUnixTimeMilliseconds();

        const string charlieReply = "Hello Alice! Received loud and clear via Relay.";
        var replyResult = await charlie.ChatService.SendMessageAsync(new SendMessageCommand(
            channelId,
            charlie.IdentityId,
            alice.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.RelayedOneToOne,
            charlieReply));

        replyResult.IsSuccess.Should().BeTrue();
        var charliePayloadId = replyResult.Value;

        // Charlie outbox dispatches to Relay
        await charlie.OutboxWorker.ProcessBatchAsync();
        relay.MailboxQueue.TotalCount.Should().Be(1);

        // Alice drains mailbox and receives reply
        await alice.DrainMailboxAsync(relay);
        relay.MailboxQueue.TotalCount.Should().Be(0);

        // Assert Alice's ChatQueries & ConversationQueries (Strict CQRS)
        var aliceMessages = await alice.ChatQueries.GetMessagesAsync(channelId, null, 50, replyNowMs);
        aliceMessages.Should().HaveCount(2);

        aliceMessages[0].Id.Should().Be(alicePayloadId);
        aliceMessages[0].AuthorId.Should().Be(alice.IdentityId);
        aliceMessages[0].Content.Should().Be(aliceMessage);

        aliceMessages[1].Id.Should().Be(charliePayloadId);
        aliceMessages[1].AuthorId.Should().Be(charlie.IdentityId);
        aliceMessages[1].Content.Should().Be(charlieReply);

        var aliceConvs = await alice.ConversationQueries.GetConversationsAsync(alice.IdentityId, replyNowMs);
        aliceConvs.Should().ContainSingle();
        aliceConvs[0].UnreadCount.Should().Be(1);
        aliceConvs[0].LastAuthorId.Should().Be(charlie.IdentityId);
        aliceConvs[0].LastMessageSnippet.Should().Be(charlieReply);
    }
}
