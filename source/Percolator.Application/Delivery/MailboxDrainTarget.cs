using Percolator.Domain.Delivery.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Delivery;

/// <summary>
/// One relay mailbox the drain pipeline is responsible for: which local persona owns
/// it, which relay hosts it, and where to reach that relay.
/// </summary>
public sealed record MailboxDrainTarget
{
    public PublicIdentityId OwnerIdentityId { get; init; }
    public PublicIdentityId RelayIdentityId { get; init; }
    public RelayEndpoint Endpoint { get; init; }

    public MailboxDrainTarget(
        PublicIdentityId OwnerIdentityId,
        PublicIdentityId RelayIdentityId,
        RelayEndpoint Endpoint)
    {
        if (!OwnerIdentityId.IsValid)
        {
            throw new ArgumentException("OwnerIdentityId must be valid.", nameof(OwnerIdentityId));
        }

        if (!RelayIdentityId.IsValid)
        {
            throw new ArgumentException("RelayIdentityId must be valid.", nameof(RelayIdentityId));
        }

        this.OwnerIdentityId = OwnerIdentityId;
        this.RelayIdentityId = RelayIdentityId;
        this.Endpoint = Endpoint ?? throw new ArgumentNullException(nameof(Endpoint));
    }

    public void Deconstruct(
        out PublicIdentityId ownerIdentityId,
        out PublicIdentityId relayIdentityId,
        out RelayEndpoint endpoint)
    {
        ownerIdentityId = OwnerIdentityId;
        relayIdentityId = RelayIdentityId;
        endpoint = Endpoint;
    }
}
