using Percolator.Application2.Delivery;
using Percolator.Application2.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Events;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.Delivery;

[TestFixture]
public sealed class OutboxWorkerTests
{
    private InMemoryOutboxRepository _outboxRepo = null!;
    private FakeTransportDispatcher _dispatcher = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private OutboxWorker _worker = null!;

    private ChannelId _channelId;
    private PublicIdentityId _aliceId;
    private PublicIdentityId _bobId;

    [SetUp]
    public void SetUp()
    {
        _outboxRepo = new InMemoryOutboxRepository();
        _dispatcher = new FakeTransportDispatcher();
        _timeProvider = new TestDateTimeProvider();
        _worker = new OutboxWorker(_outboxRepo, _dispatcher, _timeProvider);

        _channelId = ChannelId.New();
        _aliceId = PublicIdentityId.New();
        _bobId = PublicIdentityId.New();
    }

    [Test]
    public async Task ProcessBatchAsync_WhenTransportSucceeds_MarksJobDelivered()
    {
        // Arrange
        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, null, null);
        var job = OutboxJob.Create(_channelId, _aliceId, _bobId, route, "Hello"u8.ToArray(), _timeProvider).Value!;
        await _outboxRepo.SaveAsync(job);

        // Act
        int processed = await _worker.ProcessBatchAsync();

        // Assert
        processed.Should().Be(1);
        job.Status.Should().Be(OutboxStatus.Delivered);
        _dispatcher.DispatchedJobs.Should().HaveCount(1);
    }

    [Test]
    public async Task ProcessBatchAsync_WhenTransientFailure_SchedulesBackoff()
    {
        // Arrange
        _dispatcher.ShouldSucceed = false;
        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, null, null);
        var job = OutboxJob.Create(_channelId, _aliceId, _bobId, route, "Hello"u8.ToArray(), _timeProvider).Value!;
        await _outboxRepo.SaveAsync(job);

        // Act
        int processed = await _worker.ProcessBatchAsync();

        // Assert
        processed.Should().Be(1);
        job.Status.Should().Be(OutboxStatus.Pending);
        job.RetryCount.Should().Be(1);
        job.NextAttemptAtUtc.Should().BeAfter(_timeProvider.UtcNow);
    }

    [Test]
    public async Task ProcessBatchAsync_WhenMaxRetriesExceeded_MarksJobFailed()
    {
        // Arrange
        _dispatcher.ShouldSucceed = false;
        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, null, null);
        var job = OutboxJob.Create(_channelId, _aliceId, _bobId, route, "Hello"u8.ToArray(), _timeProvider, maxAttempts: 1).Value!;
        await _outboxRepo.SaveAsync(job);

        // Act
        int processed = await _worker.ProcessBatchAsync();

        // Assert
        processed.Should().Be(1);
        job.Status.Should().Be(OutboxStatus.Failed);
        job.RetryCount.Should().Be(1);
        job.NextAttemptAtUtc.Should().BeNull();
    }

    [Test]
    public void CalculateBackoff_IncreasesExponentiallyWithJitter()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;

        // Act
        var backoff0 = OutboxRetryPolicy.CalculateNextAttempt(0, now);
        var backoff1 = OutboxRetryPolicy.CalculateNextAttempt(1, now);
        var backoff2 = OutboxRetryPolicy.CalculateNextAttempt(2, now);

        // Assert
        backoff0.Should().BeAfter(now);
        backoff1.Should().BeAfter(backoff0);
        backoff2.Should().BeAfter(backoff1);
    }

    [Test]
    public async Task DormancyEventListener_TransitionsAllIdentityJobsToPausedDormant()
    {
        // Arrange
        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, null, null);
        var job1 = OutboxJob.Create(_channelId, _aliceId, _bobId, route, "Job1"u8.ToArray(), _timeProvider).Value!;
        var job2 = OutboxJob.Create(_channelId, _aliceId, _bobId, route, "Job2"u8.ToArray(), _timeProvider).Value!;
        await _outboxRepo.SaveAsync(job1);
        await _outboxRepo.SaveAsync(job2);

        var listener = new DormancyEventListener(_outboxRepo, _timeProvider);

        // Act
        await listener.HandleAsync(new IdentityDisabledEvent(_aliceId, _timeProvider.UtcNow));

        // Assert
        job1.Status.Should().Be(OutboxStatus.PausedDormant);
        job2.Status.Should().Be(OutboxStatus.PausedDormant);
    }

    [Test]
    public async Task ProcessBatchAsync_IgnoresJobsAlreadyInFlightOrPaused()
    {
        // Arrange
        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, null, null);
        var inFlightJob = OutboxJob.Create(_channelId, _aliceId, _bobId, route, "InFlight"u8.ToArray(), _timeProvider).Value!;
        inFlightJob.MarkInFlight();

        var pausedJob = OutboxJob.Create(_channelId, _aliceId, _bobId, route, "Paused"u8.ToArray(), _timeProvider).Value!;
        pausedJob.PauseForDormancy(_timeProvider);

        var readyJob = OutboxJob.Create(_channelId, _aliceId, _bobId, route, "Ready"u8.ToArray(), _timeProvider).Value!;

        await _outboxRepo.SaveAsync(inFlightJob);
        await _outboxRepo.SaveAsync(pausedJob);
        await _outboxRepo.SaveAsync(readyJob);

        // Act
        int processed = await _worker.ProcessBatchAsync();

        // Assert: Only the ready job is picked up
        processed.Should().Be(1);
        _dispatcher.DispatchedJobs.Should().HaveCount(1);
        _dispatcher.DispatchedJobs[0].Id.Should().Be(readyJob.Id);
        readyJob.Status.Should().Be(OutboxStatus.Delivered);
        inFlightJob.Status.Should().Be(OutboxStatus.InFlight);
        pausedJob.Status.Should().Be(OutboxStatus.PausedDormant);
    }
}
