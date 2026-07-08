using Percolator.Identity;
using Percolator.Network;

namespace Percolator.Application.Network;

public interface IRouteConfirmationService
{
    Task RecordAttemptAsync(
        SelfId selfIdentityId,
        Percolator.Network.NetworkPeerId remoteNetworkPeerId,
        RouteKind routeKind,
        string? endpointHost,
        int? endpointPort,
        NetworkPeerId? relayHostPeerId,
        bool success,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task PromoteToConfirmedAsync(
        SelfId selfIdentityId,
        Percolator.Network.NetworkPeerId remoteNetworkPeerId,
        RouteKind routeKind,
        string? endpointHost,
        int? endpointPort,
        NetworkPeerId? relayHostPeerId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}
