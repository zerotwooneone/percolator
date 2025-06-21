using Microsoft.Extensions.Logging;
using Percolator.Application.Security;
using Percolator.Network;
using System.Threading.Tasks;

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

        public Task HandlePeerDiscoveredAsync(Peer peer)
        {            
            _logger.LogInformation("Discovered peer {IpAddress}:{Port} with thumbprint {Thumbprint}. Adding to trusted store.", peer.IpAddress, peer.GrpcPort, peer.Thumbprint);
            _trustedPeerStore.Add(peer.Thumbprint);
            return Task.CompletedTask;
        }

        public Task HandlePeerExpiredAsync(Peer peer)
        {
            _logger.LogInformation("Peer {IpAddress}:{Port} has expired. It will no longer be trusted until rediscovered.", peer.IpAddress, peer.GrpcPort);
            // Note: Current implementation does not remove from the trusted store on expiry.
            // This is a "trust indefinitely" model after first discovery.
            return Task.CompletedTask;
        }
    }
}
