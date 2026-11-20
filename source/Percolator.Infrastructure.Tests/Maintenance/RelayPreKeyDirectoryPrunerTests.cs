using Percolator.Application.Delivery.Events;

namespace Percolator.Infrastructure.Tests.Maintenance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Infrastructure.Maintenance;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class RelayPreKeyDirectoryPrunerTests
{
    [Test]
    public void Constructor_WithNullPublisher_ThrowsArgumentNullException()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var act = () => new RelayPreKeyDirectoryPruner(factory, null!, TimeProvider.System);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task StalePreKeys_OlderThan30Days_Purged()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(t0);
        var publisher = new FakeEventPublisher();
        var pruner = new RelayPreKeyDirectoryPruner(factory, publisher, timeProvider);

        var staleToken = Guid.NewGuid().ToByteArray();
        var activeToken = Guid.NewGuid().ToByteArray();

        testDb.DbContext.RelayPreKeys.AddRange(
            new RelayPreKeyEntity
            {
                RoutingToken = staleToken,
                DeviceId = 1,
                IdentityKey = new byte[] { 1 },
                SignedPreKey = new byte[] { 2 },
                SignedPreKeySignature = new byte[] { 3 },
                OneTimePreKeysJson = "[]",
                UpdatedAtUtc = t0
            },
            new RelayPreKeyEntity
            {
                RoutingToken = activeToken,
                DeviceId = 1,
                IdentityKey = new byte[] { 4 },
                SignedPreKey = new byte[] { 5 },
                SignedPreKeySignature = new byte[] { 6 },
                OneTimePreKeysJson = "[]",
                UpdatedAtUtc = t0.AddDays(15)
            }
        );
        await testDb.DbContext.SaveChangesAsync();

        // Advance clock by 31 days: cutoff is t0 + 1 day
        timeProvider.Advance(TimeSpan.FromDays(31));

        int purgedCount = await pruner.SweepAsync();

        purgedCount.Should().Be(1);
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<RelayPreKeysPrunedEvent>()
            .Which.PurgedCount.Should().Be(1);

        var remaining = await testDb.DbContext.RelayPreKeys.ToListAsync();
        remaining.Should().ContainSingle(k => k.RoutingToken.SequenceEqual(activeToken));
        remaining.Any(k => k.RoutingToken.SequenceEqual(staleToken)).Should().BeFalse();
    }

    [Test]
    public async Task StalePreKeys_PurgedInBatches()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(t0);
        var publisher = new FakeEventPublisher();
        var pruner = new RelayPreKeyDirectoryPruner(factory, publisher, timeProvider);

        const int staleCount = 1100;
        const int activeCount = 5;

        var staleKeys = new List<RelayPreKeyEntity>(staleCount);
        for (int i = 0; i < staleCount; i++)
        {
            staleKeys.Add(new RelayPreKeyEntity
            {
                RoutingToken = Guid.NewGuid().ToByteArray(),
                DeviceId = (uint)(i + 1),
                IdentityKey = new byte[] { 1 },
                SignedPreKey = new byte[] { 2 },
                SignedPreKeySignature = new byte[] { 3 },
                OneTimePreKeysJson = "[]",
                UpdatedAtUtc = t0.AddHours(-1)
            });
        }

        var activeKeys = new List<RelayPreKeyEntity>(activeCount);
        for (int i = 0; i < activeCount; i++)
        {
            activeKeys.Add(new RelayPreKeyEntity
            {
                RoutingToken = Guid.NewGuid().ToByteArray(),
                DeviceId = (uint)(100000 + i),
                IdentityKey = new byte[] { 4 },
                SignedPreKey = new byte[] { 5 },
                SignedPreKeySignature = new byte[] { 6 },
                OneTimePreKeysJson = "[]",
                UpdatedAtUtc = t0.AddDays(20)
            });
        }

        testDb.DbContext.RelayPreKeys.AddRange(staleKeys);
        testDb.DbContext.RelayPreKeys.AddRange(activeKeys);
        await testDb.DbContext.SaveChangesAsync();

        // Advance clock by 31 days: cutoff is t0 + 1 day
        timeProvider.Advance(TimeSpan.FromDays(31));

        int totalPurged = await pruner.SweepAsync();

        totalPurged.Should().Be(staleCount);
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<RelayPreKeysPrunedEvent>()
            .Which.PurgedCount.Should().Be(staleCount);

        var remaining = await testDb.DbContext.RelayPreKeys.ToListAsync();
        remaining.Should().HaveCount(activeCount);
        remaining.All(k => k.UpdatedAtUtc > t0.AddDays(1)).Should().BeTrue();
    }

    [Test]
    public async Task SweepAsync_WhenZeroPurged_DoesNotPublishEvent()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(t0);
        var publisher = new FakeEventPublisher();
        var pruner = new RelayPreKeyDirectoryPruner(factory, publisher, timeProvider);

        int purgedCount = await pruner.SweepAsync();

        purgedCount.Should().Be(0);
        publisher.PublishedEvents.Should().BeEmpty();
    }
}
