namespace Percolator.Infrastructure.Tests.Persistence.Outbox;

using System;
using System.Text;
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
public class SqliteOutboxRepositoryTests
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
    public async Task EnqueueAndDequeue_TransitionsStateCleanly()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, new Uri("http://localhost:5000"), null);
        byte[] payload = Encoding.UTF8.GetBytes("EncryptedPayloadBlob");

        var job = OutboxJob.Rehydrate(
            jobId,
            channelId,
            ownerId,
            null,
            route,
            payload,
            OutboxStatus.Pending,
            retryCount: 0,
            maxAttempts: 5,
            createdAtUtc: DateTimeOffset.UtcNow,
            nextAttemptAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act - Save pending job
        await _repository.SaveAsync(job);

        var retrieved = await _repository.GetByIdAsync(jobId);
        retrieved.Should().NotBeNull();
        retrieved!.Status.Should().Be(OutboxStatus.Pending);
        retrieved.Payload.ToArray().Should().Equal(payload);

        // Transition to InFlight
        var inFlightJob = OutboxJob.Rehydrate(
            jobId,
            channelId,
            ownerId,
            null,
            route,
            payload,
            OutboxStatus.InFlight,
            retryCount: 1,
            maxAttempts: 5,
            createdAtUtc: job.CreatedAtUtc,
            nextAttemptAtUtc: DateTimeOffset.UtcNow.AddSeconds(30));

        await _repository.SaveAsync(inFlightJob);

        var retrievedInFlight = await _repository.GetByIdAsync(jobId);
        retrievedInFlight.Should().NotBeNull();
        retrievedInFlight!.Status.Should().Be(OutboxStatus.InFlight);
        retrievedInFlight.RetryCount.Should().Be(1);
    }

    [Test]
    public async Task GetDuePendingJobsAsync_ReturnsOnlyDuePendingJobs_OrderedByNextAttempt()
    {
        // Arrange
        var ownerId = PublicIdentityId.New();
        var channelId = ChannelId.New();
        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, null, null);
        var now = DateTimeOffset.UtcNow;

        // Due Pending job 1 (due 10 min ago)
        var dueJob1 = OutboxJob.Rehydrate(
            Guid.NewGuid(), channelId, ownerId, null, route,
            new byte[] { 1 }, OutboxStatus.Pending, 0, 5, now.AddMinutes(-20), now.AddMinutes(-10));

        // Due Pending job 2 (due 5 min ago)
        var dueJob2 = OutboxJob.Rehydrate(
            Guid.NewGuid(), channelId, ownerId, null, route,
            new byte[] { 2 }, OutboxStatus.Pending, 0, 5, now.AddMinutes(-20), now.AddMinutes(-5));

        // Future Pending job (due in 10 min)
        var futureJob = OutboxJob.Rehydrate(
            Guid.NewGuid(), channelId, ownerId, null, route,
            new byte[] { 3 }, OutboxStatus.Pending, 0, 5, now, now.AddMinutes(10));

        // InFlight job
        var inFlightJob = OutboxJob.Rehydrate(
            Guid.NewGuid(), channelId, ownerId, null, route,
            new byte[] { 4 }, OutboxStatus.InFlight, 0, 5, now, now.AddMinutes(-10));

        // Delivered job
        var deliveredJob = OutboxJob.Rehydrate(
            Guid.NewGuid(), channelId, ownerId, null, route,
            new byte[] { 5 }, OutboxStatus.Delivered, 0, 5, now, now.AddMinutes(-10));

        await _repository.SaveAsync(dueJob1);
        await _repository.SaveAsync(dueJob2);
        await _repository.SaveAsync(futureJob);
        await _repository.SaveAsync(inFlightJob);
        await _repository.SaveAsync(deliveredJob);

        // Act
        var dueJobs = await _repository.GetDuePendingJobsAsync(now, limit: 10);

        // Assert - Only dueJob1 and dueJob2 returned, ordered by NextAttemptAtUtc
        dueJobs.Should().HaveCount(2);
        dueJobs[0].Id.Should().Be(dueJob1.Id);
        dueJobs[1].Id.Should().Be(dueJob2.Id);
    }

    [Test]
    public async Task SaveAsync_WhenDelivered_ScrubsPayloadBytesInStorage()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, null, null);
        byte[] payload = Encoding.UTF8.GetBytes("SensibleSecretPayloadData");

        var deliveredJob = OutboxJob.Rehydrate(
            jobId,
            channelId,
            ownerId,
            null,
            route,
            payload,
            OutboxStatus.Delivered,
            retryCount: 1,
            maxAttempts: 5,
            createdAtUtc: DateTimeOffset.UtcNow,
            nextAttemptAtUtc: null);

        // Act - Save as Delivered
        await _repository.SaveAsync(deliveredJob);

        // Assert - Raw SQLite column is scrubbed to 0 bytes
        await using var cmd = _testDb.Connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM ops_outbox_jobs WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", jobId);
        var rawPayload = (byte[]?)await cmd.ExecuteScalarAsync();
        rawPayload.Should().NotBeNull();
        rawPayload!.Length.Should().Be(0);

        // Assert - CQRS query service still projects summary cleanly
        var summary = await _queryService.GetJobSummaryByIdAsync(jobId);
        summary.Should().NotBeNull();
        summary!.Id.Should().Be(jobId);
        summary.Status.Should().Be(OutboxStatus.Delivered);
    }
}
