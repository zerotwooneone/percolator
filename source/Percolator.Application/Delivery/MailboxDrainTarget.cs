using Percolator.Domain.Delivery.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Delivery;

/// <summary>
/// One relay mailbox the drain pipeline is responsible for: which local persona owns
/// it, which relay hosts it, and where to reach that relay.
/// </summary>
public sealed record MailboxDrainTarget(
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RelayIdentityId,
    RelayEndpoint Endpoint);
