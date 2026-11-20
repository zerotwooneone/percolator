namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// Persisted mailbox queue entry — the store-side counterpart of the wire
/// <c>QueuedSealedEnvelopeProto</c>. The queue id is the relay-assigned
/// acknowledgement key; the payload is the opaque serialized sealed envelope,
/// which the relay never parses. Mapping to/from proto happens only at the
/// gRPC boundary.
/// </summary>
public sealed record RelayQueuedEnvelopeRecord(
    MailboxQueueId QueueId,
    ReadOnlyMemory<byte> EnvelopePayload,
    DateTimeOffset EnqueuedAtUtc);
