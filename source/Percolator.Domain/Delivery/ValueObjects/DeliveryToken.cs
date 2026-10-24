using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// 32-byte authorization token presented when delivering messages into a recipient's mailbox.
/// Implements Signal Sealed Sender delivery authorization: holding the recipient's ProfileKey authorizes
/// message enqueue without revealing the sender's identity.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record DeliveryToken
{
    public static DeliveryToken DeriveFromProfileKey(IdentityKey ownerPublicKey, ProfileKey profileKey, ICryptoEngine engine)
    {
        ArgumentNullException.ThrowIfNull(ownerPublicKey);
        ArgumentNullException.ThrowIfNull(profileKey);
        ArgumentNullException.ThrowIfNull(engine);

        var bytes = engine.HkdfSha256(
            ownerPublicKey.Span,
            profileKey.Span,
            "PercolatorDeliveryTokenV1"u8,
            32);

        return FromSpan(bytes);
    }
}
