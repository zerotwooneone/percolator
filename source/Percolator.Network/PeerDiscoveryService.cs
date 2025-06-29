using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using System.Security;

namespace Percolator.Network
{
    public class PeerDiscoveryService : IDisposable, IPeerDiscoveryService
    {
        private readonly UdpClient _udpClient;
        private readonly IPeerDiscoveryConfig _config;
        private readonly IPAddress _localIpAddress;
        private readonly ConcurrentDictionary<IPEndPoint, Peer> _peers = new();
        private readonly IPeerDiscoveryHandler _handler;
        private readonly IDiscoverySignatureProvider _signatureProvider;
        private readonly IIdentityProvider _identityProvider;
        private readonly ILogger<PeerDiscoveryService> _logger;
        private CancellationTokenSource? _cancellationTokenSource;

        public PeerDiscoveryService(IPeerDiscoveryConfig config, IIdentityProvider identityProvider, IPeerDiscoveryHandler handler, IDiscoverySignatureProvider signatureProvider, ILogger<PeerDiscoveryService> logger)
        {
            _config = config;
            _identityProvider = identityProvider;
            _handler = handler;
            _signatureProvider = signatureProvider;
            _logger = logger;
            _localIpAddress = GetPrimaryLocalIpAddress();
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _config.BroadcastPort));
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
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _config.BroadcastPort);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var payload = $"{_localIpAddress}:{_config.ListenPort}";
                    var payloadBytes = Encoding.UTF8.GetBytes(payload);
                    var signature = _signatureProvider.Sign(payloadBytes);
                    var publicKeyCert = _signatureProvider.GetPublicKeyCertificate();

                    var message = $"PERCOLATOR_DISCOVERY:{Convert.ToBase64String(publicKeyCert)}:{Convert.ToBase64String(signature)}:{payload}";
                    var data = Encoding.UTF8.GetBytes(message);
                    await _udpClient.SendAsync(data, data.Length, broadcastEndpoint);
                    await Task.Delay(_config.BroadcastInterval, token);
                }
                catch (OperationCanceledException)
                {
                    // This is expected on shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred while broadcasting presence.");
                }
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
                    var parts = message.Split(':', 4);

                    if (parts.Length == 4 && parts[0] == "PERCOLATOR_DISCOVERY")
                    {
                        var publicKeyCertB64 = parts[1];
                        var signatureB64 = parts[2];
                        var payload = parts[3];
                        var payloadParts = payload.Split(':');

                        if (IPAddress.TryParse(payloadParts[0], out var discoveredIp) && int.TryParse(payloadParts[1], out var discoveredPort))
                        {
                            var publicKeyCert = Convert.FromBase64String(publicKeyCertB64);
                            var signature = Convert.FromBase64String(signatureB64);
                            var payloadBytes = Encoding.UTF8.GetBytes(payload);

                            if (!_signatureProvider.Verify(payloadBytes, signature, publicKeyCert))
                            {
                                throw new SecurityException($"Received a discovery broadcast with an invalid signature from {result.RemoteEndPoint}.");
                            }

                            var thumbprint = _signatureProvider.GetThumbprint(publicKeyCert);

                            // Ignore our own broadcast
                            if (thumbprint == _identityProvider.GetThumbprint())
                            {
                                continue;
                            }

                            var peerEndpoint = new IPEndPoint(discoveredIp, discoveredPort);
                            var peer = new Peer(PeerId.NewId(), discoveredIp, discoveredPort, thumbprint);
                            if (_peers.TryAdd(peerEndpoint, peer))
                            {
                                _logger.LogInformation("Discovered new peer {PeerEndpoint} with thumbprint {Thumbprint}", peer.GrpcEndpoint, peer.Thumbprint);
                                await _handler.HandlePeerDiscoveredAsync(peer);
                            }
                            else if (_peers.TryGetValue(peerEndpoint, out var existingPeer))
                            {
                                existingPeer.LastSeenUtc = DateTime.UtcNow;
                            }
                        }
                    }
                }
                catch (SecurityException)
                {
                    // A packet with an invalid signature was received. Ignore it and continue.
                    // This prevents a malformed packet from a malicious actor from crashing the listener.
                }
                catch (OperationCanceledException)
                {
                    // This is expected on shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred while listening for peers.");
                }
            }
        }

        private async Task RunCleanupLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_config.PeerExpiration / 2, token);
                var expiredPeers = _peers.Where(p => (DateTime.UtcNow - p.Value.LastSeenUtc) > _config.PeerExpiration).ToList();
                foreach (var expiredPeer in expiredPeers)
                {
                    if (_peers.TryRemove(expiredPeer.Key, out var removedPeer))
                    {
                        _logger.LogInformation("Peer {PeerEndpoint} expired and was removed.", removedPeer.GrpcEndpoint);
                        await _handler.HandlePeerExpiredAsync(removedPeer);
                    }
                }
            }
        }

        private static IPAddress GetPrimaryLocalIpAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip;
                }
            }
            throw new Exception("No network adapters with an IPv4 address in the system!");
        }

        public void Dispose()
        {
            _udpClient.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    }
}
