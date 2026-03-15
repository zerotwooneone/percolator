using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using System.Collections.Concurrent;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.PeerDiscovery;

public class PeerConnectionManager : IPeerConnectionManager
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
    private readonly ILogger<PeerConnectionManager> _logger;
    private readonly IPeerRoutingProfileRepository _profileRepository;
    private readonly IProfileRoutePlanner _routePlanner;

    public PeerConnectionManager(ILogger<PeerConnectionManager> logger, IPeerRoutingProfileRepository profileRepository, IProfileRoutePlanner routePlanner)
    {
        _logger = logger;
        _profileRepository = profileRepository;
        _routePlanner = routePlanner;
    }

    public async Task<TransportService.TransportServiceClient> GetTransportClient(IdentityPeerId peerId)
    {
        var profile = await _profileRepository.GetByIdAsync(new NetworkPeerId(peerId.Value)).ConfigureAwait(false)
            ?? throw new ArgumentException($"No routing profile found for peer with ID '{peerId}'.", nameof(peerId));
        var selection = _routePlanner.SelectRoute(profile);
        if (selection.Relay is not null)
        {
            throw new ArgumentException($"Relay-only route selected for peer '{peerId}'; direct client not available.", nameof(peerId));
        }
        var endpoint = selection.Endpoint;
        var targetUrl = $"https://{endpoint.EndPoint}";

        var channel = _channels.GetOrAdd(targetUrl, url =>
        {
            var handler = new HttpClientHandler();
            // TODO: use TLS certificate from peer connection
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            _logger.LogInformation("Creating gRPC channel for peer at target {TargetUrl}", url);
            return GrpcChannel.ForAddress(url, new GrpcChannelOptions
            {
                HttpHandler = handler
            });
        });

        return new TransportService.TransportServiceClient(channel);
    }

    public async Task RemovePeer(IdentityPeerId peerId)
    {
        var profile = await _profileRepository.GetByIdAsync(new NetworkPeerId(peerId.Value)).ConfigureAwait(false);
        if (profile is null)
        {
            _logger.LogWarning("Attempted to remove a peer with no routing profile: ID '{PeerId}'.", peerId);
            return;
        }
        var selection = _routePlanner.SelectRoute(profile);
        if (selection.Relay is not null)
        {
            _logger.LogInformation("Peer {PeerId} currently routes via relay; no direct channel to remove.", peerId);
            return;
        }
        var targetUrl = $"https://{selection.Endpoint.EndPoint}";
        if (_channels.TryRemove(targetUrl, out var channel))
        {
            _logger.LogInformation("Disposing gRPC channel for peer {PeerId} at {TargetUrl}", peerId, targetUrl);
            channel.Dispose();
        }
    }
}
