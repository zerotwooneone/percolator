namespace Percolator.Infrastructure.Tests.Maintenance;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Events;
using Percolator.Infrastructure.Maintenance;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class OutboxRetentionPrunerTests
{
    [Test]
    public void Constructor_WithNullPublisher_ThrowsArgumentNullException()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var act = () => new OutboxRetentionPruner(factory, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task DeliveredJobs_OlderThan7Days_Purged()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(t0);
        var publisher = new FakeEventPublisher();
        var pruner = new OutboxRetentionPruner(factory, publisher, timeProvider);

        var oldDeliveredId = Guid.NewGuid();
        var recentDeliveredId = Guid.NewGuid();

        testDb.DbContext.OpsOutboxJobs.AddRange(
            new OpsOutboxJobEntity
            {
                Id = oldDeliveredId,
                OwnerIdentityId = Guid.NewGuid(),
                ChannelId = Guid.NewGuid(),
                Status = (int)OutboxStatus.Delivered,
                CreatedAtUtc = t0,
                Payload = Array.Empty<byte>()
            },
            new OpsOutboxJobEntity
            {
                Id = recentDeliveredId,
                OwnerIdentityId = Guid.NewGuid(),
                ChannelId = Guid.NewGuid(),
                Status = (int)OutboxStatus.Delivered,
                CreatedAtUtc = t0.AddDays(3),
                Payload = Array.Empty<byte>()
            }
        );
        await testDb.DbContext.SaveChangesAsync();

        // Advance clock by 8 days: cutoff is t0 + 1 day
        timeProvider.Advance(TimeSpan.FromDays(8));

        int purgedCount = await pruner.SweepAsync();

        purgedCount.Should().Be(1);
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<OutboxJobsPrunedEvent>()
            .Which.PurgedCount.Should().Be(1);

        var remainingJobs = await testDb.DbContext.OpsOutboxJobs.ToListAsync();
        remainingJobs.Should().ContainSingle(j => j.Id == recentDeliveredId);
        remainingJobs.Any(j => j.Id == oldDeliveredId).Should().BeFalse();
    }

    [Test]
    public async Task ExactRetentionBoundary_PurgesJobAtCutoff_PreservesJobOneSecondAfterCutoff()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var t0 = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(t0);
        var publisher = new FakeEventPublisher();
        var pruner = new OutboxRetentionPruner(factory, publisher, timeProvider);

        var cutoffTime = t0 - TimeSpan.FromDays(7);
        var atCutoffId = Guid.NewGuid();
        var oneSecAfterCutoffId = Guid.NewGuid();

        testDb.DbContext.OpsOutboxJobs.AddRange(
            new OpsOutboxJobEntity
            {
                Id = atCutoffId,
                OwnerIdentityId = Guid.NewGuid(),
                ChannelId = Guid.NewGuid(),
                Status = (int)OutboxStatus.Delivered,
                CreatedAtUtc = cutoffTime,
                Payload = Array.Empty<byte>()
            },
            new OpsOutboxJobEntity
            {
                Id = oneSecAfterCutoffId,
                OwnerIdentityId = Guid.NewGuid(),
                ChannelId = Guid.NewGuid(),
                Status = (int)OutboxStatus.Delivered,
                CreatedAtUtc = cutoffTime.AddSeconds(1),
                Payload = Array.Empty<byte>()
            }
        );
        await testDb.DbContext.SaveChangesAsync();

        int purgedCount = await pruner.SweepAsync();

        purgedCount.Should().Be(1);
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<OutboxJobsPrunedEvent>()
            .Which.PurgedCount.Should().Be(1);

        var remaining = await testDb.DbContext.OpsOutboxJobs.ToListAsync();
        remaining.Should().ContainSingle(j => j.Id == oneSecAfterCutoffId);
        remaining.Any(j => j.Id == atCutoffId).Should().BeFalse();
    }

    [Test]
    public async Task FailedAndPendingJobs_PreservedRegardlessOfAge()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(t0);
        var publisher = new FakeEventPublisher();
        var pruner = new OutboxRetentionPruner(factory, publisher, timeProvider);

        var pendingId = Guid.NewGuid();
        var inFlightId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var pausedId = Guid.NewGuid();
        var deliveredId = Guid.NewGuid();

        testDb.DbContext.OpsOutboxJobs.AddRange(
            new OpsOutboxJobEntity
            {
                Id = pendingId,
                OwnerIdentityId = Guid.NewGuid(),
                ChannelId = Guid.NewGuid(),
                Status = (int)OutboxStatus.Pending,
                CreatedAtUtc = t0,
                Payload = new byte[] { 1, 2, 3 }
            },
            new OpsOutboxJobEntity
            {
                Id = inFlightId,
                OwnerIdentityId = Guid.NewGuid(),
                ChannelId = Guid.NewGuid(),
                Status = (int)OutboxStatus.InFlight,
                CreatedAtUtc = t0,
                Payload = new byte[] { 4, 5, 6 }
            },
            new OpsOutboxJobEntity
            {
                Id = failedId,
                OwnerIdentityId = Guid.NewGuid(),
                ChannelId = Guid.NewGuid(),
                Status = (int)OutboxStatus.Failed,
                CreatedAtUtc = t0,
                Payload = new byte[] { 7, 8, 9 }
            },
            new OpsOutboxJobEntity
            {
                Id = pausedId,
                OwnerIdentityId = Guid.NewGuid(),
                ChannelId = Guid.NewGuid(),
                Status = (int)OutboxStatus.PausedDormant,
                CreatedAtUtc = t0,
                Payload = new byte[] { 10 }
            },
            new OpsOutboxJobEntity
            {
                Id = deliveredId,
                OwnerIdentityId = Guid.NewGuid(),
                ChannelId = Guid.NewGuid(),
                Status = (int)OutboxStatus.Delivered,
                CreatedAtUtc = t0,
                Payload = Array.Empty<byte>()
            }
        );
        await testDb.DbContext.SaveChangesAsync();

        // Advance clock by 30 days
        timeProvider.Advance(TimeSpan.FromDays(30));

        int purgedCount = await pruner.SweepAsync();

        purgedCount.Should().Be(1);
        publisher.PublishedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<OutboxJobsPrunedEvent>()
            .Which.PurgedCount.Should().Be(1);

        var remainingJobs = await testDb.DbContext.OpsOutboxJobs.ToListAsync();
        remainingJobs.Should().HaveCount(4);
        remainingJobs.Select(j => j.Id).Should().Contain(new[] { pendingId, inFlightId, failedId, pausedId });
        remainingJobs.Any(j => j.Id == deliveredId).Should().BeFalse();
    }

    [Test]
    public async Task SweepAsync_WhenZeroPurged_DoesNotPublishEvent()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(t0);
        var publisher = new FakeEventPublisher();
        var pruner = new OutboxRetentionPruner(factory, publisher, timeProvider);

        int purgedCount = await pruner.SweepAsync();

        purgedCount.Should().Be(0);
        publisher.PublishedEvents.Should().BeEmpty();
    }

    [Test]
    public async Task SweepAsync_WhenCancelled_ReturnsImmediately()
    {
        using var testDb = new SqliteTestDb();
        var factory = new TestDbContextFactory(testDb);

        var publisher = new FakeEventPublisher();
        var pruner = new OutboxRetentionPruner(factory, publisher);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        int purgedCount = await pruner.SweepAsync(cts.Token);

        purgedCount.Should().Be(0);
        publisher.PublishedEvents.Should().BeEmpty();
    }
}
