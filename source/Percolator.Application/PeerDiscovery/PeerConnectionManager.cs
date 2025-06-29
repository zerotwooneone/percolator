using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Identity;
using System.Collections.Concurrent;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.PeerDiscovery;

public class PeerConnectionManager : IPeerConnectionManager
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
    private readonly ILogger<PeerConnectionManager> _logger;
    private readonly IPeerRepository _peerRepository;

    public PeerConnectionManager(ILogger<PeerConnectionManager> logger, IPeerRepository peerRepository)
    {
        _logger = logger;
        _peerRepository = peerRepository;
    }

    public async Task<TransportService.TransportServiceClient> GetTransportClient(IdentityPeerId peerId)
    {
        var peer = await _peerRepository.GetByIdAsync(peerId.Value);
        if (peer is null)
        {
            throw new ArgumentException($"Peer with ID '{peerId}' not found.", nameof(peerId));
        }

        var targetUrl = $"https://{peer.IpAddress}:{peer.GrpcEndpoint.Port}";

        var channel = _channels.GetOrAdd(targetUrl, url =>
        {
            var handler = new HttpClientHandler();
            // This callback is the key to our P2P trust model.
            // We only trust peers whose certificate thumbprint matches the one from discovery.
            handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                if (cert is null)
                {
                    return false;
                }
                return string.Equals(peer.Thumbprint, cert.GetCertHashString(), StringComparison.OrdinalIgnoreCase);
            };

            _logger.LogInformation("Creating gRPC channel for peer {IpAddress}:{Port} at target {TargetUrl}", peer.IpAddress, peer.GrpcEndpoint.Port, url);
            return GrpcChannel.ForAddress(url, new GrpcChannelOptions
            {
                HttpHandler = handler
            });
        });

        return new TransportService.TransportServiceClient(channel);
    }

    public async Task RemovePeer(IdentityPeerId peerId)
    {
        var peer = await _peerRepository.GetByIdAsync(peerId.Value);
        if (peer is null)
        {
            _logger.LogWarning("Attempted to remove a non-existent peer with ID '{PeerId}'.", peerId);
            return;
        }

        var targetUrl = $"https://{peer.IpAddress}:{peer.GrpcEndpoint.Port}";
        if (_channels.TryRemove(targetUrl, out var channel))
        {
            _logger.LogInformation("Disposing gRPC channel for peer {IpAddress}:{Port}", peer.IpAddress, peer.GrpcEndpoint.Port);
            channel.Dispose();
        }
    }
}
