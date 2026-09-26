using Percolator.Domain.Delivery.Client;
using Percolator.Domain.Delivery.Events;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Delivery;

[TestFixture]
public class OutboxJobTests
{
    private FakeDateTimeProvider _timeProvider = null!;
    private PublicIdentityId _ownerId;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        _ownerId = PublicIdentityId.New();
    }

    [Test]
    public void Enqueue_WhenCreated_HasPendingStatus_AndEmitsEnqueuedEvent()
    {
        var route = new DeliveryRoute(DeliveryRouteType.RelayedGroup, DirectEndpoint: null, BlindedRoutingToken.New());
        var payload = new byte[] { 1, 2, 3 };

        var job = OutboxJob.Create(_ownerId, route, payload, _timeProvider).Value;

        job.Status.Should().Be(OutboxStatus.Pending);
        job.OwnerIdentityId.Should().Be(_ownerId);
        job.DomainEvents.Should().ContainSingle(e => e is OutboxJobEnqueuedEvent);
    }

    [Test]
    public void PauseForDormancy_TransitionsToPausedDormant_AndEmitsPausedEvent()
    {
        var route = new DeliveryRoute(DeliveryRouteType.RelayedOneToOne, DirectEndpoint: null, BlindedRoutingToken.New());
        var job = OutboxJob.Create(_ownerId, route, new byte[] { 1 }, _timeProvider).Value;
        job.ClearDomainEvents();

        var result = job.PauseForDormancy(_timeProvider);

        result.IsSuccess.Should().BeTrue();
        job.Status.Should().Be(OutboxStatus.PausedDormant);
        job.DomainEvents.Should().ContainSingle(e => e is OutboxJobPausedEvent);
    }

    [Test]
    public void ResumeFromDormancy_WhenPaused_TransitionsBackToPending()
    {
        var route = new DeliveryRoute(DeliveryRouteType.RelayedOneToOne, DirectEndpoint: null, BlindedRoutingToken.New());
        var job = OutboxJob.Create(_ownerId, route, new byte[] { 1 }, _timeProvider).Value;
        job.PauseForDormancy(_timeProvider);
        job.ClearDomainEvents();

        var result = job.ResumeFromDormancy();

        result.IsSuccess.Should().BeTrue();
        job.Status.Should().Be(OutboxStatus.Pending);
    }

    [Test]
    public void MarkDelivered_TransitionsToDelivered_AndEmitsDeliveredEvent()
    {
        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, new Uri("tcp://127.0.0.1:5000"), TargetToken: null);
        var job = OutboxJob.Create(_ownerId, route, new byte[] { 1 }, _timeProvider).Value;
        job.ClearDomainEvents();

        job.MarkInFlight();
        job.Status.Should().Be(OutboxStatus.InFlight);

        var result = job.MarkDelivered(_timeProvider);

        result.IsSuccess.Should().BeTrue();
        job.Status.Should().Be(OutboxStatus.Delivered);
        job.DomainEvents.Should().ContainSingle(e => e is OutboxJobDeliveredEvent);
    }
}
