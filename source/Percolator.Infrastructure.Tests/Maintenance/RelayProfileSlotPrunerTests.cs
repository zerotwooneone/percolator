namespace Percolator.Infrastructure.Tests.Maintenance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Application2.Delivery.Events;
using Percolator.Infrastructure.Maintenance;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class RelayProfileSlotPrunerTests
{
    [Test]
    public void Constructor_WithNullPublisher_ThrowsArgumentNullException()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var act = () => new RelayProfileSlotPruner(factory, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task ExpiredSlots_PurgedInBatches()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(t0);
        var publisher = new FakeEventPublisher();
        var pruner = new RelayProfileSlotPruner(factory, publisher, timeProvider);

        const int expiredCount = 1200;
        const int activeCount = 5;

        var expiredSlots = new List<RelayProfileSlotEntity>(expiredCount);
        for (int i = 0; i < expiredCount; i++)
        {
            var locator = new byte[32];
            BitConverter.GetBytes(i).CopyTo(locator, 0);

            expiredSlots.Add(new RelayProfileSlotEntity
            {
                BlindedLocator = locator,
                WriteTokenHash = new byte[32],
                Ciphertext = new byte[100],
                Nonce = new byte[12],
                Revision = 1,
                ExpiresAtUtc = t0.AddHours(-2).AddSeconds(i)
            });
        }

        var activeSlots = new List<RelayProfileSlotEntity>(activeCount);
        for (int i = 0; i < activeCount; i++)
        {
            var locator = new byte[32];
            BitConverter.GetBytes(100000 + i).CopyTo(locator, 0);

            activeSlots.Add(new RelayProfileSlotEntity
            {
                BlindedLocator = locator,
                WriteTokenHash = new byte[32],
                Ciphertext = new byte[100],
                Nonce = new byte[12],
                Revision = 1,
                ExpiresAtUtc = t0.AddHours(2)
            });
        }

        testDb.DbContext.RelayProfileSlots.AddRange(expiredSlots);
        testDb.DbContext.RelayProfileSlots.AddRange(activeSlots);
        await testDb.DbContext.SaveChangesAsync();

        int totalPurged = await pruner.SweepAsync();

        totalPurged.Should().Be(expiredCount);
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<RelayProfileSlotsPrunedEvent>()
            .Which.PurgedCount.Should().Be(expiredCount);

        var remaining = await testDb.DbContext.RelayProfileSlots.ToListAsync();
        remaining.Should().HaveCount(activeCount);
        remaining.All(s => s.ExpiresAtUtc > t0).Should().BeTrue();
    }

    [Test]
    public async Task SweepAsync_WhenZeroPurged_DoesNotPublishEvent()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(t0);
        var publisher = new FakeEventPublisher();
        var pruner = new RelayProfileSlotPruner(factory, publisher, timeProvider);

        int purgedCount = await pruner.SweepAsync();

        purgedCount.Should().Be(0);
        publisher.PublishedEvents.Should().BeEmpty();
    }
}
