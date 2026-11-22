namespace Percolator.Application.Invitations;

using System.Collections.Generic;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

public sealed record PeerInvitationPayload(
    PublicIdentityId IdentityId,
    BlindedRoutingToken RoutingToken,
    DeliveryToken DeliveryToken,
    IReadOnlyList<string> EndpointHosts,
    PortRange? PortRange,
    RelayEndpoint? RelayEndpoint = null);
