using Percolator.Identity;
using Percolator.Network;
using PeerId = Percolator.Network.PeerId;

namespace Percolator.Application.Network;

public interface IRouteConfirmationService
{
    Task RecordAttemptAsync(
        SelfId selfIdentityId,
        Percolator.Network.PeerId remotePeerId,
        RouteKind routeKind,
        string? endpointHost,
        int? endpointPort,
        PeerId? relayHostPeerId,
        bool success,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task PromoteToConfirmedAsync(
        SelfId selfIdentityId,
        Percolator.Network.PeerId remotePeerId,
        RouteKind routeKind,
        string? endpointHost,
        int? endpointPort,
        PeerId? relayHostPeerId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}
