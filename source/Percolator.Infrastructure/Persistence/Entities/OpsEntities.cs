namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class OpsOutboxJobEntity
{
    public long SequenceNumber { get; set; }
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

/// <summary>
/// Persist-then-ack spool row for relayed mailbox envelopes (DEC-PENDING-INGRESS-SPOOL).
/// Holds ciphertext only — plaintext never reaches this table.
/// </summary>
public sealed class OpsPendingIngressEnvelopeEntity
{
    /// <summary>PK AUTOINCREMENT — preserves relay receive order for replay scans.</summary>
    public long SequenceNumber { get; set; }
    public Guid OwnerIdentityId { get; set; }
    /// <summary>Opaque relay-assigned ack key (<c>MailboxQueueId.Value</c>) — the (OwnerIdentityId, QueueId) dedup key.</summary>
    public string QueueId { get; set; } = string.Empty;
    public byte[] RoutingToken { get; set; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public DateTimeOffset EnqueuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorDescription { get; set; }
}
