using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Percolator.NetworkTests")]

namespace Percolator.Network
{
    public class PeerDiscoveryService : IDisposable
    {
        private const int BroadcastPort = 8888;
        private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PeerExpirationTime = TimeSpan.FromSeconds(30);

        private readonly UdpClient _udpClient;
        private readonly int _grpcPort;
        private readonly IPAddress _localIpAddress;
        private readonly ConcurrentDictionary<IPEndPoint, Peer> _peers = new();
        private readonly IPeerDiscoveryHandler _handler;
        private CancellationTokenSource? _cancellationTokenSource;

        public IReadOnlyCollection<Peer> DiscoveredPeers => _peers.Values.ToList().AsReadOnly();

        public PeerDiscoveryService(int grpcPort, IPeerDiscoveryHandler handler)
        {
            _grpcPort = grpcPort;
            _handler = handler;
            _localIpAddress = GetPrimaryLocalIpAddress();
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, BroadcastPort));
            _udpClient.EnableBroadcast = true;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var listenTask = ListenForPeersAsync(_cancellationTokenSource.Token);
            var broadcastTask = BroadcastPresenceAsync(_cancellationTokenSource.Token);
            var cleanupTask = RunCleanupLoopAsync(_cancellationTokenSource.Token);

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
                var message = $"PERCOLATOR_DISCOVERY:{_localIpAddress}:{_grpcPort}";
                var data = Encoding.UTF8.GetBytes(message);
                await _udpClient.SendAsync(data, new IPEndPoint(IPAddress.Broadcast, BroadcastPort), token);
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
                    var parts = message.Split(':');

                    if (parts.Length == 3 && parts[0] == "PERCOLATOR_DISCOVERY")
                    {
                        if (IPAddress.TryParse(parts[1], out var discoveredIp) && int.TryParse(parts[2], out var discoveredPort))
                        {
                            // Ignore our own broadcast
                            if (discoveredIp.Equals(_localIpAddress) && discoveredPort == _grpcPort)
                            {
                                continue;
                            }

                            var peerEndpoint = new IPEndPoint(discoveredIp, discoveredPort);

                            _peers.AddOrUpdate(peerEndpoint,
                                // Factory for adding a new peer
                                (key) =>
                                {
                                    var newPeer = new Peer(discoveredIp, discoveredPort);
                                    _ = _handler.HandlePeerDiscoveredAsync(newPeer);
                                    return newPeer;
                                },
                                // Factory for updating an existing peer
                                (key, existingPeer) =>
                                {
                                    existingPeer.LastSeenUtc = DateTime.UtcNow;
                                    return existingPeer;
                                });
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Discovery] Error while listening for peers: {ex.Message}");
                }
            }
        }

        private async Task RunCleanupLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    CleanupExpiredPeers();

                    await Task.Delay(BroadcastInterval, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break; // Exit loop on cancellation
                }
            }
        }

        internal void AddPeerForTesting(Peer peer)
        {
            _peers[peer.GrpcEndpoint] = peer;
        }

        internal void CleanupExpiredPeers()
        {
            var expiredPeers = _peers.Values.Where(p => (DateTime.UtcNow - p.LastSeenUtc) > PeerExpirationTime).ToList();
            foreach (var peer in expiredPeers)
            {
                if (_peers.TryRemove(peer.GrpcEndpoint, out var removedPeer))
                {
                    Console.WriteLine($"[Discovery] Peer expired: {removedPeer}");
                    _ = _handler.HandlePeerExpiredAsync(removedPeer);
                }
            }
        }

        private static IPAddress GetPrimaryLocalIpAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var primaryIp = host.AddressList
                .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

            return primaryIp ?? throw new Exception("Could not determine primary local IP address for discovery.");
        }

        public void Dispose()
        {
            Stop();
            _udpClient?.Dispose();
        }
    }
}
