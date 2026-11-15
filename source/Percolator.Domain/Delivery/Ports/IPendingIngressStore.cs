using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Delivery.Ports;

/// <summary>
/// Durable per-owner spool for relayed envelopes received but not yet ingested
/// (DEC-PENDING-INGRESS-SPOOL). Persist-before-ack makes relay redelivery after a
/// crash safe: the dedup key is (OwnerIdentityId, QueueId).
/// </summary>
public interface IPendingIngressStore
{
    /// <summary>
    /// Idempotent insert keyed on (OwnerIdentityId, QueueId). Returns the canonical
    /// stored record — on a dedup hit the pre-existing row with its persisted attempt
    /// count — so callers enforce the retry budget against stored state, not the
    /// redelivered envelope.
    /// </summary>
    Task<PendingIngressRecord> UpsertAsync(
        PublicIdentityId ownerId,
        QueuedMailboxEnvelope envelope,
        CancellationToken ct = default);

    /// <summary>All spooled records for an owner in receive order.</summary>
    Task<IReadOnlyList<PendingIngressRecord>> ListByOwnerAsync(
        PublicIdentityId ownerId,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the row if present, zeroizing the persisted ciphertext copy first.
    /// No-op when the row is absent.
    /// </summary>
    Task DeleteAsync(
        PublicIdentityId ownerId,
        MailboxQueueId queueId,
        CancellationToken ct = default);

    /// <summary>
    /// Increments the attempt counter and stores the latest transient error.
    /// No-op when the row is absent (concurrently resolved).
    /// </summary>
    Task RecordTransientFailureAsync(
        PublicIdentityId ownerId,
        MailboxQueueId queueId,
        DomainError error,
        CancellationToken ct = default);
}
