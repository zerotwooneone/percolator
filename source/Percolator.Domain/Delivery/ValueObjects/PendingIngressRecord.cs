using Percolator.Domain.Common;

namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// A relayed envelope durably spooled in the local pending-ingress store along with its
/// retry bookkeeping. <see cref="LastError"/> carries the most recent transient failure
/// for diagnostics; receive order comes from store sequence, not the record itself.
/// </summary>
public sealed record PendingIngressRecord
{
    public MailboxQueueId QueueId { get; init; }
    public QueuedMailboxEnvelope Queued { get; init; }
    public int Attempts { get; init; }
    public DomainError? LastError { get; init; }

    public PendingIngressRecord(
        MailboxQueueId QueueId,
        QueuedMailboxEnvelope Queued,
        int Attempts,
        DomainError? LastError)
    {
        this.QueueId = QueueId ?? throw new ArgumentNullException(nameof(QueueId));
        this.Queued = Queued ?? throw new ArgumentNullException(nameof(Queued));
        if (Attempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Attempts), "Attempts cannot be negative.");
        }

        this.Attempts = Attempts;
        this.LastError = LastError;
    }

    public void Deconstruct(
        out MailboxQueueId queueId,
        out QueuedMailboxEnvelope queued,
        out int attempts,
        out DomainError? lastError)
    {
        queueId = QueueId;
        queued = Queued;
        attempts = Attempts;
        lastError = LastError;
    }
}
