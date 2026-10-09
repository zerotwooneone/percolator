using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Routing;

public enum DeliveryRoutingMode
{
    DirectPeer = 1,
    RelayMailbox = 2
}

public readonly record struct ResolvedDeliveryRoute(
    DeliveryRoutingMode Mode,
    PublicIdentityId TargetPeerId,
    PublicIdentityId? TargetRelayId = null);

public interface IRoutingCoordinator
{
    ValueTask<DomainResult<ResolvedDeliveryRoute>> ResolveRouteAsync(
        PublicIdentityId targetPeerId,
        CancellationToken ct = default);
}

public interface IPeerReachabilityService
{
    ValueTask<bool> IsDirectlyReachableAsync(PublicIdentityId peerId, CancellationToken ct = default);
    ValueTask<PublicIdentityId?> GetHomeRelayAsync(PublicIdentityId peerId, CancellationToken ct = default);
}

public sealed class RoutingCoordinator : IRoutingCoordinator
{
    private readonly IStreamRegistry _streamRegistry;
    private readonly IPeerReachabilityService _reachabilityService;

    public RoutingCoordinator(
        IStreamRegistry streamRegistry,
        IPeerReachabilityService reachabilityService)
    {
        _streamRegistry = streamRegistry ?? throw new ArgumentNullException(nameof(streamRegistry));
        _reachabilityService = reachabilityService ?? throw new ArgumentNullException(nameof(reachabilityService));
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

        // 2. Check if peer endpoint is directly reachable
        bool directlyReachable = await _reachabilityService.IsDirectlyReachableAsync(targetPeerId, ct);
        if (directlyReachable)
        {
            return DomainResult<ResolvedDeliveryRoute>.Success(
                new ResolvedDeliveryRoute(DeliveryRoutingMode.DirectPeer, targetPeerId));
        }

        // 3. Fallback to relay mailbox
        var relayId = await _reachabilityService.GetHomeRelayAsync(targetPeerId, ct);
        if (relayId != null)
        {
            return DomainResult<ResolvedDeliveryRoute>.Success(
                new ResolvedDeliveryRoute(DeliveryRoutingMode.RelayMailbox, targetPeerId, relayId));
        }

        return DomainResult<ResolvedDeliveryRoute>.Failure(new DomainError(
            "UNRESOLVABLE_ROUTE", $"Unable to resolve direct route or home relay for peer {targetPeerId}."));
    }
}
