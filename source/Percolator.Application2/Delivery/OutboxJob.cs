using Percolator.Application2.Delivery.Events;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Delivery;

public sealed class OutboxJob : AggregateRoot<Guid>
{
    public override Guid Id { get; }
    public PublicIdentityId OwnerIdentityId { get; }
    public DeliveryRoute Route { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public OutboxStatus Status { get; private set; }
    public int RetryCount { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    private OutboxJob(
        Guid id,
        PublicIdentityId ownerIdentityId,
        DeliveryRoute route,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        OwnerIdentityId = ownerIdentityId;
        Route = route;
        Payload = payload;
        Status = OutboxStatus.Pending;
        RetryCount = 0;
        CreatedAtUtc = createdAtUtc;
        NextAttemptAtUtc = createdAtUtc;
    }

    public static DomainResult<OutboxJob> Create(
        PublicIdentityId ownerIdentityId,
        DeliveryRoute route,
        ReadOnlyMemory<byte> payload,
        IDateTimeProvider timeProvider,
        Guid? id = null)
    {
        var job = new OutboxJob(id ?? Guid.NewGuid(), ownerIdentityId, route, payload, timeProvider.UtcNow);
        job.AddDomainEvent(new OutboxJobEnqueuedEvent(job.Id, ownerIdentityId, timeProvider.UtcNow));
        return DomainResult<OutboxJob>.Success(job);
    }

    public DomainResult PauseForDormancy(IDateTimeProvider timeProvider)
    {
        if (Status == OutboxStatus.Delivered)
        {
            return DomainResult.Success();
        }

        Status = OutboxStatus.PausedDormant;
        AddDomainEvent(new OutboxJobPausedEvent(Id, OwnerIdentityId, timeProvider.UtcNow));
        return DomainResult.Success();
    }

    public DomainResult ResumeFromDormancy()
    {
        if (Status == OutboxStatus.PausedDormant)
        {
            Status = OutboxStatus.Pending;
        }

        return DomainResult.Success();
    }

    public void MarkInFlight()
    {
        Status = OutboxStatus.InFlight;
    }

    public DomainResult MarkDelivered(IDateTimeProvider timeProvider)
    {
        Status = OutboxStatus.Delivered;
        AddDomainEvent(new OutboxJobDeliveredEvent(Id, timeProvider.UtcNow));
        return DomainResult.Success();
    }

    public void RecordFailure(DateTimeOffset nextRetryUtc)
    {
        RetryCount++;
        Status = OutboxStatus.Failed;
        NextAttemptAtUtc = nextRetryUtc;
    }
}
