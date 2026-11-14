using System;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Delivery.Model;

/// <summary>
/// Binding between a local owner identity and a mailbox hosted on a relay.
/// Carries the per-(OwnerIdentityId, RelayIdentityId) blinded routing token and the
/// drain token authorizing mailbox registration/draining on that relay.
/// </summary>
public sealed record RelayMailboxBinding(
    PublicIdentityId OwnerIdentityId,
    BlindedRoutingToken RoutingToken,
    DeliveryToken DrainToken,
    DateTimeOffset LinkedAtUtc,
    DeliveryToken? PreviousDrainToken = null);
