namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// A mailbox envelope paired with its relay queue identifier — the key used for
/// acknowledgement and local deduplication across reconnects.
/// </summary>
public sealed record QueuedMailboxEnvelope(MailboxQueueId QueueId, MailboxEnvelope Envelope);
