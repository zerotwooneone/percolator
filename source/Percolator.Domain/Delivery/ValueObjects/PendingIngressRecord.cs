using Percolator.Domain.Common;

namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// A relayed envelope durably spooled in the local pending-ingress store along with its
/// retry bookkeeping. <see cref="LastError"/> carries the most recent transient failure
/// for diagnostics; receive order comes from store sequence, not the record itself.
/// </summary>
public sealed record PendingIngressRecord(
    MailboxQueueId QueueId,
    QueuedMailboxEnvelope Queued,
    int Attempts,
    DomainError? LastError);
