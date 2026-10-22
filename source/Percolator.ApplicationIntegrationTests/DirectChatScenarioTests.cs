using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2IntegrationTests.Builders;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Identities.ValueObjects;
using DeliveryRoute = Percolator.PluginSdk.DeliveryRoute;

namespace Percolator.Application2IntegrationTests;

[TestFixture]
public sealed class DirectChatScenarioTests
{
    [Test]
    public async Task Phase1_Alice_And_Bob_DirectChat_Over_LoopbackPorts()
    {
        // ----------------------------------------------------------------------------------
        // Arrange:
        // 1. Build Alice (localhost:15001) and Bob (localhost:15002) via TestClusterBuilder
        // ----------------------------------------------------------------------------------
        var alicePort = new EndpointPort(15001);
        var bobPort = new EndpointPort(15002);

        var cluster = new TestClusterBuilder()
            .AddNode("Alice", alicePort)
            .AddNode("Bob", bobPort)
            .Build();

        var alice = cluster["Alice"];
        var bob = cluster["Bob"];

        // 2. Exchange InvitationLink out-of-band and verify URI format
        var aliceLink = alice.CreateInvitationLink();
        var bobLink = bob.CreateInvitationLink();

        aliceLink.Host.Should().Be("localhost");
        aliceLink.Port.Should().Be(alicePort.Value);
        aliceLink.ToString().Should().StartWith($"percolator://localhost:{alicePort.Value}/");

        bobLink.Host.Should().Be("localhost");
        bobLink.Port.Should().Be(bobPort.Value);
        bobLink.ToString().Should().StartWith($"percolator://localhost:{bobPort.Value}/");

        // 3. Establish mutual X3DH / DirectRatchetSession, shared DirectChannel, and open direct loopback stream
        var channelId = await alice.EstablishDirectSessionWithAsync(bob);
        cluster.Switch.HasActiveStream(alice.Port, bob.Port).Should().BeTrue();

        ulong nowMs = (ulong)alice.TimeProvider.UtcNow.ToUnixTimeMilliseconds();

        // ----------------------------------------------------------------------------------
        // Act 1: Alice sends initial chat message to Bob over DirectP2P
        // ----------------------------------------------------------------------------------
        const string aliceMessage = "Hello Bob over direct localhost!";
        var sendResult = await alice.ChatService.SendMessageAsync(new SendMessageCommand(
            channelId,
            alice.IdentityId,
            bob.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.DirectP2P,
            aliceMessage));

        sendResult.IsSuccess.Should().BeTrue();
        var alicePayloadId = sendResult.Value;

        // ----------------------------------------------------------------------------------
        // Assert 1 (Strict CQRS):
        // Query Alice.OutboxQueries -> verify job transitioned to Delivered via fast-path IStreamRegistry.TryWriteAsync
        // (Domain Write Repositories are strictly encapsulated and NEVER accessed by test assertions)
        // ----------------------------------------------------------------------------------
        var pendingAliceJobs = await alice.OutboxQueries.GetPendingJobsAsync();
        pendingAliceJobs.Should().BeEmpty("Fast-path stream delivery should immediately deliver the message");

        var allAliceOutboxJobs = await alice.OutboxQueries.GetJobsForOwnerAsync(alice.IdentityId);
        allAliceOutboxJobs.Should().ContainSingle();
        allAliceOutboxJobs[0].Status.Should().Be(OutboxStatus.Delivered);
        allAliceOutboxJobs[0].RecipientIdentityId.Should().Be(bob.IdentityId);

        // ----------------------------------------------------------------------------------
        // Assert 2 (Strict CQRS):
        // Query Bob.ChatQueries & Bob.ConversationQueries -> verify content decrypted & unread count is 1
        // ----------------------------------------------------------------------------------
        var bobMessages = await bob.ChatQueries.GetMessagesAsync(channelId, null, 50, nowMs);
        bobMessages.Should().ContainSingle();
        bobMessages[0].Id.Should().Be(alicePayloadId);
        bobMessages[0].AuthorId.Should().Be(alice.IdentityId);
        bobMessages[0].Content.Should().Be(aliceMessage);
        bobMessages[0].ChannelId.Should().Be(channelId);

        var bobConversations = await bob.ConversationQueries.GetConversationsAsync(bob.IdentityId, nowMs);
        bobConversations.Should().ContainSingle();
        bobConversations[0].ChannelId.Should().Be(channelId);
        bobConversations[0].UnreadCount.Should().Be(1);
        bobConversations[0].LastAuthorId.Should().Be(alice.IdentityId);
        bobConversations[0].LastMessageSnippet.Should().Be(aliceMessage);

        // ----------------------------------------------------------------------------------
        // Act 2: Bob replies back to Alice, triggering Double Ratchet DH turnaround
        // ----------------------------------------------------------------------------------
        cluster.AdvanceTime(TimeSpan.FromSeconds(1));
        ulong replyNowMs = (ulong)bob.TimeProvider.UtcNow.ToUnixTimeMilliseconds();

        const string bobReply = "Hi Alice, loud and clear!";
        var replyResult = await bob.ChatService.SendMessageAsync(new SendMessageCommand(
            channelId,
            bob.IdentityId,
            alice.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.DirectP2P,
            bobReply));

        replyResult.IsSuccess.Should().BeTrue();
        var bobPayloadId = replyResult.Value;

        // ----------------------------------------------------------------------------------
        // Assert 3 (Strict CQRS):
        // Query Alice.ChatQueries -> verify both messages appear in chronological/causal order
        // after the Double Ratchet DH turnaround (StepDhRatchet)
        // ----------------------------------------------------------------------------------
        var aliceMessages = await alice.ChatQueries.GetMessagesAsync(channelId, null, 50, replyNowMs);
        aliceMessages.Should().HaveCount(2);

        aliceMessages[0].Id.Should().Be(alicePayloadId);
        aliceMessages[0].AuthorId.Should().Be(alice.IdentityId);
        aliceMessages[0].Content.Should().Be(aliceMessage);

        aliceMessages[1].Id.Should().Be(bobPayloadId);
        aliceMessages[1].AuthorId.Should().Be(bob.IdentityId);
        aliceMessages[1].Content.Should().Be(bobReply);

        // Query Alice.ConversationQueries -> verify unread count is 1 (for Bob's reply)
        var aliceConversations = await alice.ConversationQueries.GetConversationsAsync(alice.IdentityId, replyNowMs);
        aliceConversations.Should().ContainSingle();
        aliceConversations[0].ChannelId.Should().Be(channelId);
        aliceConversations[0].UnreadCount.Should().Be(1);
        aliceConversations[0].LastAuthorId.Should().Be(bob.IdentityId);
        aliceConversations[0].LastMessageSnippet.Should().Be(bobReply);
    }
}
