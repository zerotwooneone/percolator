namespace Percolator.Infrastructure.Tests.Persistence.Outbox;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Delivery;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class SqlOutboxQueryServiceTests
{
    private SqliteTestDb _testDb = null!;
    private SqliteOutboxRepository _repository = null!;
    private SqlOutboxQueryService _queryService = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqliteOutboxRepository(_testDb.DbContext);
        _queryService = new SqlOutboxQueryService(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task GetOutboxSummary_ProjectsLightweightModel()
    {
        // Arrange
        var ownerId = PublicIdentityId.New();
        var channelId = ChannelId.New();
        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, null, null);

        var pendingJob = OutboxJob.Rehydrate(
            Guid.NewGuid(), channelId, ownerId, null, route,
            new byte[] { 10, 20, 30 }, OutboxStatus.Pending, 0, 5,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var failedJob = OutboxJob.Rehydrate(
            Guid.NewGuid(), channelId, ownerId, null, route,
            new byte[] { 40, 50, 60 }, OutboxStatus.Failed, 5, 5,
            DateTimeOffset.UtcNow, null);

        await _repository.SaveAsync(pendingJob);
        await _repository.SaveAsync(failedJob);

        // Act
        var pendingList = await _queryService.GetPendingJobsAsync();
        var failedList = await _queryService.GetFailedJobsAsync();
        var ownerJobs = await _queryService.GetJobsForOwnerAsync(ownerId);

        // Assert
        pendingList.Should().ContainSingle(j => j.Id == pendingJob.Id);
        failedList.Should().ContainSingle(j => j.Id == failedJob.Id);
        ownerJobs.Should().HaveCount(2);
    }
}
