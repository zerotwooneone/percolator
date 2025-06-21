using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

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
        private CancellationTokenSource? _cancellationTokenSource;

        public event EventHandler<Peer>? PeerDiscovered;
        public event EventHandler<Peer>? PeerExpired;

        public IReadOnlyCollection<Peer> DiscoveredPeers => _peers.Values.ToList();

        public PeerDiscoveryService(int grpcPort)
        {
            _grpcPort = grpcPort;
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
                            var resultingPeer = new Peer(discoveredIp, discoveredPort);

                            if (_peers.TryAdd(peerEndpoint, resultingPeer))
                            {
                                PeerDiscovered?.Invoke(this, resultingPeer);
                                _ = Task.Delay(PeerExpirationTime, token).ContinueWith(_ =>
                                {
                                    if (_peers.TryRemove(peerEndpoint, out var removedPeer))
                                    {
                                        PeerExpired?.Invoke(this, removedPeer);
                                    }
                                }, token);
                            }
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

        private async Task CleanupExpiredPeersAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var expiredPeers = _peers.Values.Where(p => DateTime.UtcNow - p.LastSeenUtc > PeerExpirationTime).ToList();

                    foreach (var peer in expiredPeers)
                    {
                        if (_peers.TryRemove(peer.GrpcEndpoint, out var removedPeer))
                        {
                            Console.WriteLine($"[Discovery] Peer expired: {removedPeer}");
                            PeerExpired?.Invoke(this, removedPeer);
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

        private IPAddress GetPrimaryLocalIpAddress()
        {
            var primaryIp = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                              ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .FirstOrDefault(ip => ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?
                .Address;

            return primaryIp ?? throw new Exception("Could not determine primary local IP address for discovery.");
        }

        public void Dispose()
        {
            Stop();
            _udpClient?.Dispose();
        }
    }
}
