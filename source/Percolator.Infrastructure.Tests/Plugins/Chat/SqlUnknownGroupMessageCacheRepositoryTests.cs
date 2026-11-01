namespace Percolator.Infrastructure.Tests.Plugins.Chat;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Apps.Chat.Ingress;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqlUnknownGroupMessageCacheRepositoryTests
{
    [Test]
    public async Task CacheEnvelope_Exceeding500Cap_EvictsOldestAndPrunesAt24Hours()
    {
        using var testDb = new SqliteTestDb();
        var repo = new SqlUnknownGroupMessageCacheRepository(testDb.DbContext);

        var channelId = ChannelId.New();
        var authorId = PublicIdentityId.New();
        byte[] payloadBytes = new byte[] { 1, 2, 3, 4 };

        // 1. Insert 505 envelopes exceeding the 500 cap
        for (ulong i = 0; i < 505; i++)
        {
            var record = new UnknownGroupMessageRecord(
                PayloadId.New(),
                channelId,
                authorId,
                payloadBytes,
                1000UL + i,
                1000UL + i + 86_400_000UL); // +24 hours

            await repo.AddAsync(record);
        }

        // Assert count clamped to 500
        int count = await testDb.DbContext.ChatUnknownGroupMessages.CountAsync();
        count.Should().Be(500);

        // Assert oldest 5 (receivedAt 1000 to 1004) were evicted
        var oldestRemaining = await testDb.DbContext.ChatUnknownGroupMessages
            .OrderBy(m => m.ReceivedAtUnixMs)
            .FirstAsync();
        oldestRemaining.ReceivedAtUnixMs.Should().Be(1005UL);

        // 2. Test 24-hour TTL pruning
        ulong nowMs = 1000UL + 504UL + 86_400_000UL + 100UL; // Past all expirations
        int purged = await repo.PurgeExpiredAsync(nowMs);
        purged.Should().Be(500);

        int finalCount = await testDb.DbContext.ChatUnknownGroupMessages.CountAsync();
        finalCount.Should().Be(0);
    }
}
