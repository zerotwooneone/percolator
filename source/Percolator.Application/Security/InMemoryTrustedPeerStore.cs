using System.Collections.Concurrent;
using Percolator.Network;

namespace Percolator.Application.Security
{
    public class InMemoryTrustedPeerStore : ITrustedPeerStore
    {
        private readonly ConcurrentDictionary<PublicKeyHash, byte> _trustedHashes = new();

        public InMemoryTrustedPeerStore()
        {
            // The responsibility for trusting the local node's certificate is now handled by the IdentityOrchestrator.
        }

        public void Add(PublicKeyHash publicKeyHash)
        {
            _trustedHashes.TryAdd(publicKeyHash, 0);
        }

        public bool IsTrusted(PublicKeyHash publicKeyHash)
        {
            return _trustedHashes.ContainsKey(publicKeyHash);
        }
    }
}
