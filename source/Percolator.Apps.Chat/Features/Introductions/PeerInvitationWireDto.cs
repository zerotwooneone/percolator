using System;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Introductions;

public sealed record PeerInvitationWireDto(
    PublicIdentityId InviterIdentityId,
    IdentityKey InviterIdentityKey,
    RelayEndpoint RelayEndpoint,
    BlindedRoutingToken RoutingToken)
{
    public DomainResult<PeerInvitation> ToDomain()
    {
        return PeerInvitation.Create(InviterIdentityId, InviterIdentityKey, RelayEndpoint, RoutingToken);
    }

    public static PeerInvitationWireDto FromDomain(PeerInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return new PeerInvitationWireDto(
            invitation.InviterIdentityId,
            invitation.InviterIdentityKey,
            invitation.RelayEndpoint,
            invitation.RoutingToken);
    }
}
