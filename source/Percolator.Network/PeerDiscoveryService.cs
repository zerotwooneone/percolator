using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using System.Security;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Percolator.Contracts;

namespace Percolator.Network;

public class PeerDiscoveryService : IDisposable, IPeerDiscoveryService
{
    private readonly UdpClient _udpClient;
    private readonly IPeerDiscoveryConfig _config;
    private readonly ConcurrentDictionary<PublicKeyHash, DiscoveredPeer> _peers = new();
    private readonly IPeerDiscoveryHandler _handler;
    private readonly ISigningService _signingService;
    private readonly ILogger<PeerDiscoveryService> _logger;
    private CancellationTokenSource? _cancellationTokenSource;

    public PeerDiscoveryService(
        IPeerDiscoveryConfig config,
        IPeerDiscoveryHandler handler,
        ISigningService signingService,
        ILogger<PeerDiscoveryService> logger)
    {
        _config = config;
        _handler = handler;
        _signingService = signingService;
        _logger = logger;
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
                // 1. Get the public key
                var publicKey = _signingService.GetActivePublicKey();

                // 2. Create the signed payload, including the public key
                var protoPayload = new DiscoveryPayload
                {
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Port = _config.ListenPort,
                    PublicKey = ByteString.CopyFrom(publicKey.Value)
                };
                var payload = new Payload(protoPayload.ToByteArray());

                // 3. Sign the payload
                var signature = _signingService.Sign(payload);

                // 4. Construct the broadcast message
                var broadcast = new DiscoveryBroadcast
                {
                    PublicKey = ByteString.CopyFrom(publicKey.Value),
                    Signature = ByteString.CopyFrom(signature.Value),
                    Payload = ByteString.CopyFrom(payload.Value)
                };

                // 5. Serialize and send
                var data = broadcast.ToByteArray();
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
                
                // 1. Parse the incoming broadcast
                var broadcast = DiscoveryBroadcast.Parser.ParseFrom(result.Buffer);

                // 2. Wrap primitives in value types for verification
                var payload = new Payload(broadcast.Payload.ToByteArray());
                var signature = new Signature(broadcast.Signature.ToByteArray());
                var publicKey = new PublicKey(broadcast.PublicKey.ToByteArray());

                // 3. Verify the signature
                if (!_signingService.Verify(payload, signature, publicKey))
                {
                    throw new SecurityException($"Received a discovery broadcast with an invalid signature from {result.RemoteEndPoint}.");
                }

                // 4. Deserialize payload and verify the inner public key matches the outer one
                var protoPayload = DiscoveryPayload.Parser.ParseFrom(payload.Value);
                if (!publicKey.Value.SequenceEqual(protoPayload.PublicKey.ToByteArray()))
                {
                    throw new SecurityException($"Public key in broadcast wrapper does not match public key in signed payload from {result.RemoteEndPoint}.");
                }

                // 5. Get the peer's unique identifier
                var publicKeyHash = _signingService.GetHash(publicKey);

                // 6. Ignore our own broadcast
                if (publicKeyHash.Equals(_signingService.GetActivePublicKeyHash()))
                {
                    continue;
                }

                // 7. Extract peer info
                var discoveredIp = result.RemoteEndPoint.Address;
                var discoveredPort = protoPayload.Port;

                var peer = new DiscoveredPeer(PeerId.NewId(), discoveredIp, discoveredPort, publicKeyHash);
                if (_peers.TryAdd(publicKeyHash, peer))
                {
                    _logger.LogInformation("Discovered new peer {PeerEndpoint} with ID {PeerId}", peer.GrpcEndpoint, BitConverter.ToString(publicKeyHash.Value));
                    await _handler.HandlePeerDiscoveredAsync(peer);
                }
                else if (_peers.TryGetValue(publicKeyHash, out var existingPeer))
                {
                    existingPeer.LastSeenUtc = DateTime.UtcNow;
                }
            }
            catch (SecurityException ex)
            {
                _logger.LogWarning(ex.Message);
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
