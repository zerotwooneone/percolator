using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Percolator.Network
{
    public class PeerDiscoveryService : IDisposable
    {
        private const int BroadcastPort = 8888;
        private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PeerExpirationTime = TimeSpan.FromSeconds(30);

        private readonly UdpClient _udpClient;
        private readonly int _discoveryPort;
        private readonly int _grpcPort;
        private readonly ConcurrentDictionary<IPEndPoint, Peer> _peers = new();
        private CancellationTokenSource? _cancellationTokenSource;

        public PeerDiscoveryService(int grpcPort, int discoveryPort = 8999)
        {
            _grpcPort = grpcPort;
            _discoveryPort = discoveryPort;
            _udpClient = new UdpClient(_discoveryPort);
            _udpClient.EnableBroadcast = true;
        }

        public IReadOnlyCollection<Peer> DiscoveredPeers => _peers.Values.ToList();

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var listenTask = ListenForPeersAsync(_cancellationTokenSource.Token);
            var broadcastTask = BroadcastPresenceAsync(_cancellationTokenSource.Token);
            var cleanupTask = CleanupExpiredPeersAsync(_cancellationTokenSource.Token);

            try
            {
                await Task.WhenAll(listenTask, broadcastTask, cleanupTask);
            }
            catch (OperationCanceledException)
            {
                // This is expected on shutdown
            }
        }

        public void Stop()
        {
            _cancellationTokenSource?.Cancel();
        }

        private async Task BroadcastPresenceAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var message = $"PERCOLATOR_DISCOVERY:{_grpcPort}";
                var data = Encoding.UTF8.GetBytes(message);
                await _udpClient.SendAsync(data, new IPEndPoint(IPAddress.Broadcast, _discoveryPort), token);
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }

        private async Task ListenForPeersAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(token);
                    var message = Encoding.UTF8.GetString(result.Buffer);

                    if (message.StartsWith("PERCOLATOR_DISCOVERY:"))
                    {
                        var parts = message.Split(':');
                        if (parts.Length == 2 && int.TryParse(parts[1], out var receivedGrpcPort))
                        {
                            var remoteEndpoint = result.RemoteEndPoint;
                            if (IsSelf(remoteEndpoint.Address, receivedGrpcPort)) continue;

                            var peer = new Peer(remoteEndpoint.Address, receivedGrpcPort);
                            peer.LastSeenUtc = DateTime.UtcNow;

                            _peers.AddOrUpdate(peer.GrpcEndpoint, peer, (key, existingPeer) =>
                            {
                                existingPeer.LastSeenUtc = DateTime.UtcNow;
                                return existingPeer;
                            });
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // Service is stopping
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during peer discovery: {ex.Message}");
                }
            }
        }

        private async Task CleanupExpiredPeersAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var expiredPeers = _peers.Values.Where(p => DateTime.UtcNow - p.LastSeenUtc > PeerExpirationTime).ToList();

                    foreach (var peer in expiredPeers)
                    {
                        if (_peers.TryRemove(peer.GrpcEndpoint, out _))
                        {
                            Console.WriteLine($"[Discovery] Peer expired: {peer}");
                        }
                    }

                    await Task.Delay(BroadcastInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break; // Exit loop on cancellation
                }
            }
        }

        private bool IsSelf(IPAddress address, int port)
        {
            if (port != _grpcPort) return false;
            if (IPAddress.IsLoopback(address)) return true;

            var host = Dns.GetHostEntry(Dns.GetHostName());
            return host.AddressList.Any(ip => ip.AddressFamily == AddressFamily.InterNetwork && ip.Equals(address));
        }

        public void Dispose()
        {
            Stop();
            _udpClient?.Dispose();
        }
    }
}
