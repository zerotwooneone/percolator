using Microsoft.Extensions.Logging;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.Application.Network;

public sealed class RouteConfirmationService : IRouteConfirmationService
{
    private readonly IPeerRouteCandidateRepository _candidateRepository;
    private readonly IPeerRoutingProfileRepository _profileRepository;
    private readonly ILogger<RouteConfirmationService> _logger;

    public RouteConfirmationService(
        IPeerRouteCandidateRepository candidateRepository,
        IPeerRoutingProfileRepository profileRepository,
        ILogger<RouteConfirmationService> logger)
    {
        _candidateRepository = candidateRepository;
        _profileRepository = profileRepository;
        _logger = logger;
    }

    public async Task RecordAttemptAsync(
        SelfId selfIdentityId,
        Percolator.Network.PeerId remotePeerId,
        RouteKind routeKind,
        string? endpointHost,
        int? endpointPort,
        Guid? relayHostPeerId,
        bool success,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var candidates = await _candidateRepository.GetCandidatesAsync(selfIdentityId.Value, remotePeerId, cancellationToken).ConfigureAwait(false);
            var candidate = candidates.FirstOrDefault(c =>
                c.RouteKind == routeKind &&
                c.EndpointHost == endpointHost &&
                c.EndpointPort == endpointPort &&
                c.RelayHostPeerId == relayHostPeerId);

            if (candidate is not null)
            {
                candidate.AttemptCount++;
                candidate.LastAttemptAtUtc = nowUtc;
                if (success)
                {
                    candidate.LastSuccessAtUtc = nowUtc;
                    candidate.LastError = null;
                }
                else
                {
                    candidate.LastError = "Attempt failed";
                }

                await _candidateRepository.UpsertAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record route attempt for {RemotePeerId}", remotePeerId);
        }
    }

    public async Task PromoteToConfirmedAsync(
        SelfId selfIdentityId,
        Percolator.Network.PeerId remotePeerId,
        RouteKind routeKind,
        string? endpointHost,
        int? endpointPort,
        Guid? relayHostPeerId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await _profileRepository.GetByIdAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                profile = new PeerRoutingProfile();
                profile.BindIdentity(remotePeerId);
            }

            if (routeKind == RouteKind.Direct && endpointHost is not null && endpointPort.HasValue)
            {
                var endPoint = new System.Net.DnsEndPoint(endpointHost, endpointPort.Value);
                var grpcEndPoint = new GrpcEndPoint(endPoint, nowUtc);
                profile.AddGrpcEndPoint(grpcEndPoint, nowUtc);
            }
            else if (routeKind == RouteKind.Relayed && relayHostPeerId.HasValue)
            {
                var relayPeerId = new Percolator.Network.PeerId(relayHostPeerId.Value);
                
                profile.AddOrRefreshRelay(relayPeerId, nowUtc);
            }

            await _profileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to promote route to confirmed for {RemotePeerId}", remotePeerId);
        }
    }
}
