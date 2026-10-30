namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class OpsOutboxJobEntity
{
    public Guid Id { get; set; }
    public Guid OwnerIdentityId { get; set; }
    public Guid ChannelId { get; set; }
    public Guid? RecipientId { get; set; }
    public int DeliveryRouteType { get; set; }
    public string? DeliveryRouteJson { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public int Status { get; set; }
    public int RetryCount { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class OpsInboundQueueEntity
{
    public Guid Id { get; set; }
    public Guid SenderId { get; set; }
    public int AppId { get; set; }
    public byte[] RawPayload { get; set; } = Array.Empty<byte>();
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
}

public sealed class OpsReservedPortEntity
{
    public int PortNumber { get; set; }
    public Guid LeaseToken { get; set; }
    public DateTimeOffset AllocatedAtUtc { get; set; }
    public DateTimeOffset LeaseExpiryUtc { get; set; }
}
