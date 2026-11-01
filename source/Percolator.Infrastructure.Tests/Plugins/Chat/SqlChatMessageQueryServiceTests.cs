namespace Percolator.Infrastructure.Tests.Plugins.Chat;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqlChatMessageQueryServiceTests
{
    [Test]
    public async Task PagedTimeline_ClampsToLimitAndOffset()
    {
        using var testDb = new SqliteTestDb();
        var repo = new SqlChatMessageRepository(testDb.DbContext);
        var queryService = new SqlChatMessageQueryService(testDb.DbContext);

        var channelId = ChannelId.New();
        var authorId = PublicIdentityId.New();

        // Seed 10 messages with timestamps 1000 to 1009
        for (ulong i = 0; i < 10; i++)
        {
            var msg = new ChatMessage(
                PayloadId.New(),
                channelId,
                authorId,
                1000UL + i,
                $"Message {i}");

            await repo.AddAsync(msg);
        }

        // Add 1 expired message
        var expiredMsg = new ChatMessage(
            PayloadId.New(),
            channelId,
            authorId,
            1010UL,
            "Expired Message",
            expireAfterSeconds: 10,
            expiresAtUnixMs: 1020UL);
        await repo.AddAsync(expiredMsg);

        ulong currentTimestampMs = 2000UL; // after expiration

        // 1. Query latest 5 messages
        var page1 = await queryService.GetMessagesAsync(
            channelId,
            beforeTimestampMs: null,
            limit: 5,
            currentTimestampMs: currentTimestampMs);

        page1.Count.Should().Be(5);
        // Ordered chronologically: timestamps 1005, 1006, 1007, 1008, 1009
        page1[0].TimestampUnixMs.Should().Be(1005UL);
        page1[4].TimestampUnixMs.Should().Be(1009UL);

        // 2. Query next page using beforeTimestampMs = 1005
        var page2 = await queryService.GetMessagesAsync(
            channelId,
            beforeTimestampMs: 1005UL,
            limit: 5,
            currentTimestampMs: currentTimestampMs);

        page2.Count.Should().Be(5);
        // Ordered chronologically: timestamps 1000, 1001, 1002, 1003, 1004
        page2[0].TimestampUnixMs.Should().Be(1000UL);
        page2[4].TimestampUnixMs.Should().Be(1004UL);

        // 3. Clamping limit
        var smallPage = await queryService.GetMessagesAsync(
            channelId,
            beforeTimestampMs: null,
            limit: 2,
            currentTimestampMs: currentTimestampMs);

        smallPage.Count.Should().Be(2);
        smallPage[0].TimestampUnixMs.Should().Be(1008UL);
        smallPage[1].TimestampUnixMs.Should().Be(1009UL);
    }
}
