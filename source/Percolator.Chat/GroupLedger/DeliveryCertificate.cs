namespace Percolator.Chat.GroupLedger;

public sealed record DeliveryCertificate(
    DeliveryCertificatePayloadBytes Payload,
    SignatureBytes Signature,
    DateTimeOffset ExpiresAtUtc);
