using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Routing;

public enum DeliveryRoutingMode
{
    DirectPeer = 1,
    RelayMailbox = 2
}

public readonly record struct ResolvedDeliveryRoute(
    DeliveryRoutingMode Mode,
    PublicIdentityId TargetPeerId,
    string? TargetRelayEndpoint = null,
    BlindedRoutingToken? TargetRoutingToken = null,
    DeliveryToken? TargetDeliveryToken = null);

public interface IRoutingCoordinator
{
    ValueTask<DomainResult<ResolvedDeliveryRoute>> ResolveRouteAsync(
        PublicIdentityId targetPeerId,
        CancellationToken ct = default);
}

public sealed class RoutingCoordinator : IRoutingCoordinator
{
    private readonly IStreamRegistry _streamRegistry;
    private readonly IPeerMailboxRoutingRepository _routingRepo;
    private readonly IDateTimeProvider _timeProvider;

    public RoutingCoordinator(
        IStreamRegistry streamRegistry,
        IPeerMailboxRoutingRepository routingRepo,
        IDateTimeProvider timeProvider)
    {
        _streamRegistry = streamRegistry ?? throw new ArgumentNullException(nameof(streamRegistry));
        _routingRepo = routingRepo ?? throw new ArgumentNullException(nameof(routingRepo));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<DomainResult<ResolvedDeliveryRoute>> ResolveRouteAsync(
        PublicIdentityId targetPeerId,
        CancellationToken ct = default)
    {
        // 1. Check if we already have an active open direct stream to peer
        if (_streamRegistry.HasActiveStream(targetPeerId))
        {
            return DomainResult<ResolvedDeliveryRoute>.Success(
                new ResolvedDeliveryRoute(DeliveryRoutingMode.DirectPeer, targetPeerId));
        }

        // 2. Query peer routing repository for best available relay route (filtering out backpressure cooldowns)
        var bestRoute = await _routingRepo.GetBestRouteAsync(targetPeerId, _timeProvider.UtcNow, ct).ConfigureAwait(false);
        if (bestRoute != null)
        {
            return DomainResult<ResolvedDeliveryRoute>.Success(
                new ResolvedDeliveryRoute(
                    DeliveryRoutingMode.RelayMailbox,
                    targetPeerId,
                    bestRoute.RelayEndpoint,
                    bestRoute.RoutingToken,
                    bestRoute.DeliveryToken));
        }

        return DomainResult<ResolvedDeliveryRoute>.Failure(new DomainError(
            "UNRESOLVABLE_ROUTE", $"Unable to resolve direct route or available relay route for peer {targetPeerId}."));
    }
}
