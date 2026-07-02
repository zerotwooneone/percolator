using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;

namespace Percolator.Infrastructure.Chat.Persistence;

/// <summary>
/// Represents a delivery certificate issued by a relay to a local identity.
/// A delivery certificate is a short-lived authentication token used for Sealed Sender routing.
/// </summary>
public class DeliveryCertificateDbo
{
    /// <summary>
    /// Auto-incrementing Surrogate Primary Key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The local self identity ID that owns this certificate.
    /// </summary>
    public ChatSelfId SelfId { get; set; }

    /// <summary>
    /// The relay peer ID that issued this certificate.
    /// </summary>
    public ChatPeerId RelayPeerId { get; set; }

    /// <summary>
    /// The certificate payload bytes.
    /// </summary>
    public DeliveryCertificatePayloadBytes Payload { get; set; }

    /// <summary>
    /// The signature bytes.
    /// </summary>
    public SignatureBytes Signature { get; set; }

    /// <summary>
    /// The UTC timestamp when this certificate expires.
    /// </summary>
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
