using Grpc.Net.Client;
using Percolator.Contracts.Protos;
using Percolator.Network;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Application;

public class PeerConnectionManager
{
    private readonly ConcurrentDictionary<Peer, GrpcChannel> _channels = new();

    public FileSharing.FileSharingClient GetClient(Peer peer)
    {
        var channel = _channels.GetOrAdd(peer, p =>
        {
            // Force local connections to 127.0.0.1 for testing multiple nodes on one machine
            var targetIp = IPAddress.IsLoopback(p.IpAddress) ? IPAddress.Loopback.ToString() : p.IpAddress.ToString();

            var handler = new HttpClientHandler();
            // This callback is the key to our P2P trust model.
            // We are ensuring that the certificate presented by the server
            // matches the thumbprint we received during the discovery phase.
            handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                if (cert is null)
                {
                    return false;
                }

                // The thumbprint of the certificate presented by the server MUST match the
                // thumbprint we received for that peer during the discovery phase.
                // This is the core of our P2P trust model.
                Console.WriteLine($"[Security] Validating certificate from {peer.IpAddress}. Expected thumbprint: {peer.Thumbprint}, Got: {cert.GetCertHashString()}");
                return string.Equals(peer.Thumbprint, cert.GetCertHashString(), StringComparison.OrdinalIgnoreCase);
            };

            var targetUrl = $"https://{targetIp}:{p.GrpcEndpoint.Port}";
            Console.WriteLine($"[ConnectionManager] Creating gRPC channel for peer {p.IpAddress}:{p.GrpcEndpoint.Port} at target {targetUrl}");
            return GrpcChannel.ForAddress(targetUrl, new GrpcChannelOptions
            {
                HttpHandler = handler
            });
        });

        return new FileSharing.FileSharingClient(channel);
    }

    public void RemovePeer(Peer peer)
    {
        if (_channels.TryRemove(peer, out var channel))
        {
            channel.Dispose();
        }
    }
}
