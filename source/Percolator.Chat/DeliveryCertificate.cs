using Percolator.Cryptography;

namespace Percolator.Chat;

public sealed record DeliveryCertificate(
    DeliveryCertificatePayloadBytes Payload,
    Signature Signature,
    DateTimeOffset ExpiresAt);
