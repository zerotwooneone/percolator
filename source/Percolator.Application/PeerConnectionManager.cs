using Grpc.Net.Client;
using Percolator.Contracts.Protos;
using Percolator.Network;
using System.Collections.Concurrent;
using System.Net;

namespace Percolator.Application;

public class PeerConnectionManager
{
    private readonly ConcurrentDictionary<Peer, GrpcChannel> _channels = new();

    public FileSharing.FileSharingClient GetClient(Peer peer)
    {
        var channel = _channels.GetOrAdd(peer, p =>
        {
            // For local testing, always use the loopback address.
            var address = IPAddress.Loopback;
            var targetUrl = $"http://{address}:{p.GrpcEndpoint.Port}";
            Console.WriteLine($"[ConnectionManager] Creating gRPC channel for peer {p.IpAddress}:{p.GrpcEndpoint.Port} at target {targetUrl}");
            return GrpcChannel.ForAddress(targetUrl);
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
