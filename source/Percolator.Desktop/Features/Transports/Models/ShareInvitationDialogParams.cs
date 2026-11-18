using System.Collections.Generic;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Features.Transports.Models;

/// <summary>
/// Immutable input parameters required to initialize the "Share My Invitation" dialog.
/// </summary>
public sealed record ShareInvitationDialogParams(
    PublicIdentityId IdentityId,
    BlindedRoutingToken RoutingToken,
    DeliveryToken DeliveryToken,
    IReadOnlyList<string> EndpointHosts,
    int BoundPort);
