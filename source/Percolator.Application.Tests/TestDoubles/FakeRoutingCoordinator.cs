using Percolator.Application2.Routing;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

/// <summary>
/// Routing coordinator stub that always resolves peers as directly reachable.
/// </summary>
public sealed class FakeRoutingCoordinator : IRoutingCoordinator
{
    public ValueTask<DomainResult<ResolvedDeliveryRoute>> ResolveRouteAsync(
        PublicIdentityId targetPeerId,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult(DomainResult<ResolvedDeliveryRoute>.Success(
            new ResolvedDeliveryRoute(DeliveryRoutingMode.DirectPeer, targetPeerId)));
    }
}
