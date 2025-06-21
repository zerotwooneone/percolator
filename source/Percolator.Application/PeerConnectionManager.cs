using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Contracts.Protos;
using Percolator.Network;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Application;

public class PeerConnectionManager : IPeerConnectionManager
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
    private readonly ILogger<PeerConnectionManager> _logger;

    public PeerConnectionManager(ILogger<PeerConnectionManager> logger)
    {
        _logger = logger;
    }

    public FileSharing.FileSharingClient GetClient(Peer peer)
    {
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

        return new FileSharing.FileSharingClient(channel);
    }

    public void RemovePeer(Peer peer)
    {
        var targetUrl = $"https://{peer.IpAddress}:{peer.GrpcEndpoint.Port}";
        if (_channels.TryRemove(targetUrl, out var channel))
        {
            channel.Dispose();
        }
    }
}
