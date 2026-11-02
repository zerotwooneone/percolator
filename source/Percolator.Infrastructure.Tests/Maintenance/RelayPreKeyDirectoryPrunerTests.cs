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
    public async Task StalePreKeys_OlderThan30Days_Purged()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(t0);
        var pruner = new RelayPreKeyDirectoryPruner(factory, timeProvider);

        var staleOwnerId = Guid.NewGuid();
        var activeOwnerId = Guid.NewGuid();

        testDb.DbContext.RelayPreKeys.AddRange(
            new RelayPreKeyEntity
            {
                OwnerIdentityId = staleOwnerId,
                DeviceId = 1,
                IdentityKey = new byte[] { 1 },
                SignedPreKey = new byte[] { 2 },
                SignedPreKeySignature = new byte[] { 3 },
                OneTimePreKeysJson = "[]",
                UpdatedAtUtc = t0
            },
            new RelayPreKeyEntity
            {
                OwnerIdentityId = activeOwnerId,
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

        var remaining = await testDb.DbContext.RelayPreKeys.ToListAsync();
        remaining.Should().ContainSingle(k => k.OwnerIdentityId == activeOwnerId);
        remaining.Any(k => k.OwnerIdentityId == staleOwnerId).Should().BeFalse();
    }

    [Test]
    public async Task StalePreKeys_PurgedInBatches()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(t0);
        var pruner = new RelayPreKeyDirectoryPruner(factory, timeProvider);

        const int staleCount = 1100;
        const int activeCount = 5;

        var staleKeys = new List<RelayPreKeyEntity>(staleCount);
        for (int i = 0; i < staleCount; i++)
        {
            staleKeys.Add(new RelayPreKeyEntity
            {
                OwnerIdentityId = Guid.NewGuid(),
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
                OwnerIdentityId = Guid.NewGuid(),
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

        var remaining = await testDb.DbContext.RelayPreKeys.ToListAsync();
        remaining.Should().HaveCount(activeCount);
        remaining.All(k => k.UpdatedAtUtc > t0.AddDays(1)).Should().BeTrue();
    }
}
