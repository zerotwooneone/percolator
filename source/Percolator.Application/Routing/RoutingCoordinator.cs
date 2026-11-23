using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.PluginSdk.Profiles;

namespace Percolator.Application.Routing;

public enum DeliveryRoutingMode
{
    Relayed = 2
}

public readonly record struct ResolvedDeliveryRoute(
    DeliveryRoutingMode Mode,
    PublicIdentityId TargetPeerId,
    RelayEndpoint TargetRelayEndpoint,
    BlindedRoutingToken TargetRoutingToken,
    DeliveryToken? TargetDeliveryToken = null);

public interface IRoutingCoordinator
{
    ValueTask<DomainResult<ResolvedDeliveryRoute>> ResolveRouteAsync(
        PublicIdentityId targetPeerId,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default);
}

public sealed class RoutingCoordinator : IRoutingCoordinator
{
    private readonly IPeerMailboxRoutingRepository _routingRepo;
    private readonly IDateTimeProvider _timeProvider;

    public RoutingCoordinator(
        IPeerMailboxRoutingRepository routingRepo,
        IDateTimeProvider timeProvider)
    {
        _routingRepo = routingRepo ?? throw new ArgumentNullException(nameof(routingRepo));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<DomainResult<ResolvedDeliveryRoute>> ResolveRouteAsync(
        PublicIdentityId targetPeerId,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        PeerMailboxRoutingCapability? route = null;

        if (relayEndpoint != null)
        {
            route = await _routingRepo.GetRouteForExchangeAsync(targetPeerId, relayEndpoint, _timeProvider.UtcNow, ct).ConfigureAwait(false);
        }
        else
        {
            route = await _routingRepo.GetBestRouteAsync(targetPeerId, _timeProvider.UtcNow, ct).ConfigureAwait(false);
        }

        if (route != null)
        {
            return DomainResult<ResolvedDeliveryRoute>.Success(
                new ResolvedDeliveryRoute(
                    DeliveryRoutingMode.Relayed,
                    targetPeerId,
                    route.RelayEndpoint,
                    route.RoutingToken,
                    route.DeliveryToken));
        }

        return DomainResult<ResolvedDeliveryRoute>.Failure(new DomainError(
            "NO_RELAY_ROUTE_AVAILABLE", $"Unable to resolve available relay route for peer {targetPeerId}."));
    }
}
