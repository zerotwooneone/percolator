namespace Percolator.Domain.Delivery.ValueObjects;

public readonly record struct MailboxEnvelope(
    Guid Id,
    BlindedRoutingToken RecipientToken,
    ReadOnlyMemory<byte> Ciphertext,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset ExpiresAtUtc);
