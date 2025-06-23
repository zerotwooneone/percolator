using System.Collections.Concurrent;

namespace Percolator.Application.Security
{
    public class InMemoryTrustedPeerStore : ITrustedPeerStore
    {
        private readonly ConcurrentDictionary<string, byte> _trustedThumbprints = new(StringComparer.OrdinalIgnoreCase);

        public InMemoryTrustedPeerStore()
        {
            // The responsibility for trusting the local node's certificate is now handled by the IdentityOrchestrator.
        }

        public void Add(string thumbprint)
        {
            _trustedThumbprints.TryAdd(thumbprint, 0);
        }

        public bool IsTrusted(string thumbprint)
        {
            return _trustedThumbprints.ContainsKey(thumbprint);
        }
    }
}
