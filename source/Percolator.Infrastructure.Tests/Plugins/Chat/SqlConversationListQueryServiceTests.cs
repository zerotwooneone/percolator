namespace Percolator.Infrastructure.Tests.Plugins.Chat;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqlConversationListQueryServiceTests
{
    [Test]
    public async Task UnreadBadgeCount_AggregatesPastReadReceipt()
    {
        using var testDb = new SqliteTestDb();
        var repo = new SqlChatMessageRepository(testDb.DbContext);
        var convQueryService = new SqlConversationListQueryService(testDb.DbContext);

        var channelId = ChannelId.New();
        var currentUserId = PublicIdentityId.New();
        var peerId = PublicIdentityId.New();

        testDb.DbContext.ChatChannels.Add(new ChatChannelEntity
        {
            Id = channelId.Value,
            Type = 0,
            Name = "Alice & Bob",
            OwnerIdentityId = currentUserId.Value,
            RemotePeerId = peerId.Value,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        });
        await testDb.DbContext.SaveChangesAsync();

        // Msg 1 from peer: read
        var msg1 = new ChatMessage(
            PayloadId.New(), channelId, peerId, 1000UL, "Msg 1",
            readAtUnixMs: 1050UL);
        await repo.AddAsync(msg1);

        // Msg 2 from peer: read
        var msg2 = new ChatMessage(
            PayloadId.New(), channelId, peerId, 1010UL, "Msg 2",
            readAtUnixMs: 1050UL);
        await repo.AddAsync(msg2);

        // Msg 3 from peer: unread (past read receipt)
        var msg3 = new ChatMessage(
            PayloadId.New(), channelId, peerId, 1100UL, "Msg 3 unread",
            readAtUnixMs: null);
        await repo.AddAsync(msg3);

        // Msg 4 from current user: unread by peer, but current user sent it, so shouldn't count towards current user's unread badge!
        var msg4 = new ChatMessage(
            PayloadId.New(), channelId, currentUserId, 1150UL, "Msg 4 from me",
            readAtUnixMs: null);
        await repo.AddAsync(msg4);

        // Msg 5 from peer: unread (past read receipt)
        var msg5 = new ChatMessage(
            PayloadId.New(), channelId, peerId, 1200UL, "Msg 5 latest unread",
            readAtUnixMs: null);
        await repo.AddAsync(msg5);

        var summaries = await convQueryService.GetConversationsAsync(
            currentUserId,
            currentTimestampMs: 2000UL);

        summaries.Count.Should().Be(1);
        var summary = summaries[0];
        summary.ChannelId.Should().Be(channelId);
        summary.Title.Should().Be("Alice & Bob");
        summary.LastMessageSnippet.Should().Be("Msg 5 latest unread");
        summary.LastMessageTimestampUnixMs.Should().Be(1200UL);
        summary.LastAuthorId.Should().Be(peerId);
        summary.UnreadCount.Should().Be(2, "Only peer's unread messages past read receipt should count");
    }
}
