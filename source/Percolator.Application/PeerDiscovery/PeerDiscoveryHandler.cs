using Microsoft.Extensions.Logging;
using Percolator.Network;

namespace Percolator.Application.PeerDiscovery
{
    /// <summary>
    /// Acts as the bridge between the network-layer discovery service and application logic.
    /// When a peer is discovered, this handler logs the discovery.
    /// Trust is established through the X3DH/Signal protocol layer, not TLS.
    /// </summary>
    public class PeerDiscoveryHandler : IPeerDiscoveryHandler
    {
        private readonly ILogger<PeerDiscoveryHandler> _logger;

        public PeerDiscoveryHandler(ILogger<PeerDiscoveryHandler> logger)
        {
            _logger = logger;
        }

        public Task HandlePeerDiscoveredAsync(DiscoveredPeer discoveredPeer)
        {
            var ep = discoveredPeer.Endpoints.FirstOrDefault();
            var epStr = ep?.EndPoint.Host + ":" + ep?.EndPoint.Port ?? "unknown";
            _logger.LogInformation("Discovered peer {Endpoint} with public key hash {PublicKeyHash}.", epStr, discoveredPeer.IdentityPublicKeyHash);
            return Task.CompletedTask;
        }

        public Task HandlePeerExpiredAsync(DiscoveredPeer discoveredPeer)
        {
            var ep = discoveredPeer.Endpoints.FirstOrDefault();
            var epStr = ep?.EndPoint.Host + ":" + ep?.EndPoint.Port ?? "unknown";
            _logger.LogInformation("Peer {Endpoint} has expired.", epStr);
            return Task.CompletedTask;
        }
    }
}
