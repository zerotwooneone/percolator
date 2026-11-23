using Percolator.Application.Routing;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.TestDoubles;

/// <summary>
/// Routing coordinator stub that always resolves peers via relay.
/// </summary>
public sealed class FakeRoutingCoordinator : IRoutingCoordinator
{
    public ValueTask<DomainResult<ResolvedDeliveryRoute>> ResolveRouteAsync(
        PublicIdentityId targetPeerId,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult(DomainResult<ResolvedDeliveryRoute>.Success(
            new ResolvedDeliveryRoute(
                DeliveryRoutingMode.Relayed,
                targetPeerId,
                relayEndpoint ?? RelayEndpoint.Parse("127.0.0.1:5000"),
                BlindedRoutingToken.New())));
    }
}

