using Microsoft.Extensions.Logging;
using Percolator.Network;

namespace Percolator.Application.PeerDiscovery
{
    /// <summary>
    /// Acts as the bridge between the network-layer discovery service and application logic.
    /// When a peer is discovered, this handler adds it to the trusted store.
    /// </summary>
    public class PeerDiscoveryHandler : IPeerDiscoveryHandler
    {
        private readonly ILogger<PeerDiscoveryHandler> _logger;
        private readonly ITrustedPeerStore _trustedPeerStore;

        public PeerDiscoveryHandler(ILogger<PeerDiscoveryHandler> logger, ITrustedPeerStore trustedPeerStore)
        {
            _logger = logger;
            _trustedPeerStore = trustedPeerStore;
        }

        public Task HandlePeerDiscoveredAsync(DiscoveredPeer discoveredPeer)
        {            
            _logger.LogInformation("Discovered peer {Endpoint} with public key hash {PublicKeyHash}. Adding to trusted store.", discoveredPeer.GrpcEndpoint, discoveredPeer.PublicKeyHash);
            _trustedPeerStore.Add(discoveredPeer.PublicKeyHash);
            return Task.CompletedTask;
        }

        public Task HandlePeerExpiredAsync(DiscoveredPeer discoveredPeer)
        {
            _logger.LogInformation("Peer {Endpoint} has expired. It will no longer be trusted until rediscovered.", discoveredPeer.GrpcEndpoint);
            // Note: Current implementation does not remove from the trusted store on expiry.
            // This is a "trust indefinitely" model after first discovery.
            return Task.CompletedTask;
        }
    }
}
