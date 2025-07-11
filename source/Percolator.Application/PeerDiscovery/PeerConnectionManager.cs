using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Identity;
using System.Collections.Concurrent;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.PeerDiscovery;

public class PeerConnectionManager : IPeerConnectionManager
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
    private readonly ILogger<PeerConnectionManager> _logger;
    private readonly IPeerRepository _peerRepository;
    private readonly IPeerConnectionRepository _peerConnectionRepository;

    public PeerConnectionManager(ILogger<PeerConnectionManager> logger, IPeerRepository peerRepository, IPeerConnectionRepository peerConnectionRepository)
    {
        _logger = logger;
        _peerRepository = peerRepository;
        _peerConnectionRepository = peerConnectionRepository;
    }

    public async Task<TransportService.TransportServiceClient> GetTransportClient(IdentityPeerId peerId)
    {
        var connectionInfo = await _peerConnectionRepository.GetByIdAsync(new NetworkPeerId(peerId.Value));
        if (connectionInfo is null)
        {
            throw new ArgumentException($"No connection info found for peer with ID '{peerId}'.", nameof(peerId));
        }


        var grpcEndPoint = TryGetEndpoint(connectionInfo);
        if (grpcEndPoint is null)
        {
            throw new ArgumentException($"No gRPC endpoints found for peer with ID '{peerId}'.", nameof(peerId));
        }
        var targetUrl = $"https://{grpcEndPoint.EndPoint}";

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

    private static GrpcEndPoint? TryGetEndpoint(PeerConnection connectionInfo)
    {
        //todo:find a way to determine the right endpoint
        return connectionInfo.GrpcEndPoints.FirstOrDefault();
    }

    public async Task RemovePeer(IdentityPeerId peerId)
    {
        var connectionInfo = await _peerConnectionRepository.GetByIdAsync(new NetworkPeerId(peerId.Value));
        if (connectionInfo is null)
        {
            _logger.LogWarning("Attempted to remove a peer with no connection info: ID '{PeerId}'.", peerId);
            return;
        }

        var grpcEndPoint = TryGetEndpoint(connectionInfo);
        if (grpcEndPoint is null)
        {
            throw new ArgumentException($"No gRPC endpoints found for peer with ID '{peerId}'.", nameof(peerId));
        }
        var targetUrl = $"https://{grpcEndPoint.EndPoint}";
        if (_channels.TryRemove(targetUrl, out var channel))
        {
            _logger.LogInformation("Disposing gRPC channel for peer {PeerId} at {TargetUrl}", peerId, targetUrl);
            channel.Dispose();
        }
    }
}
