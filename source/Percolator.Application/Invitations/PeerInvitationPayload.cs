namespace Percolator.Application.Invitations;

using System;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

public sealed record PeerInvitationPayload(
    PublicIdentityId IdentityId,
    IdentityKey IdentityKey,
    RelayEndpoint RelayEndpoint,
    BlindedRoutingToken RoutingToken)
{
    public DomainResult<PeerInvitation> ToDomain()
    {
        return PeerInvitation.Create(IdentityId, IdentityKey, RelayEndpoint, RoutingToken);
    }

    public static PeerInvitationPayload FromDomain(PeerInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return new PeerInvitationPayload(
            invitation.InviterIdentityId,
            invitation.InviterIdentityKey,
            invitation.RelayEndpoint,
            invitation.RoutingToken);
    }
}
