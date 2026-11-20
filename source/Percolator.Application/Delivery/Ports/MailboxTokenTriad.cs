using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Application.Delivery.Ports;

/// <summary>
/// Token triad required for authenticated mailbox access against an untrusted relay.
/// Combines the recipient's blinded routing token with the delivery and drain capabilities.
/// During drain-token rotation, <paramref name="PreviousDrainToken"/> allows graceful overlap.
/// </summary>
public sealed record MailboxTokenTriad(
    BlindedRoutingToken RoutingToken,
    DeliveryToken DeliveryToken,
    DeliveryToken DrainToken,
    DeliveryToken? PreviousDrainToken = null);
