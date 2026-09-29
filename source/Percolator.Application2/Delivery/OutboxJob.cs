using Percolator.Application2.Delivery.Events;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Delivery;

public sealed class OutboxJob : AggregateRoot<Guid>
{
    public const int DefaultMaxAttempts = 5;

    public override Guid Id { get; }
    public ChannelId ChannelId { get; }
    public PublicIdentityId OwnerIdentityId { get; }
    public PublicIdentityId? RecipientIdentityId { get; }
    public DeliveryRoute Route { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public OutboxStatus Status { get; private set; }
    public int RetryCount { get; private set; }
    public int MaxAttempts { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    private OutboxJob(
        Guid id,
        ChannelId channelId,
        PublicIdentityId ownerIdentityId,
        PublicIdentityId? recipientIdentityId,
        DeliveryRoute route,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset createdAtUtc,
        int maxAttempts = DefaultMaxAttempts)
    {
        Id = id;
        ChannelId = channelId;
        OwnerIdentityId = ownerIdentityId;
        RecipientIdentityId = recipientIdentityId;
        Route = route;
        Payload = payload;
        Status = OutboxStatus.Pending;
        RetryCount = 0;
        MaxAttempts = maxAttempts;
        CreatedAtUtc = createdAtUtc;
        NextAttemptAtUtc = createdAtUtc;
    }

    public static DomainResult<OutboxJob> Create(
        ChannelId channelId,
        PublicIdentityId ownerIdentityId,
        PublicIdentityId? recipientIdentityId,
        DeliveryRoute route,
        ReadOnlyMemory<byte> payload,
        IDateTimeProvider timeProvider,
        int maxAttempts = DefaultMaxAttempts,
        Guid? id = null)
    {
        var job = new OutboxJob(
            id ?? Guid.NewGuid(),
            channelId,
            ownerIdentityId,
            recipientIdentityId,
            route,
            payload,
            timeProvider.UtcNow,
            maxAttempts);

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
        if (RetryCount >= MaxAttempts)
        {
            Status = OutboxStatus.Failed;
            NextAttemptAtUtc = null;
        }
        else
        {
            Status = OutboxStatus.Pending;
            NextAttemptAtUtc = nextRetryUtc;
        }
    }
}
