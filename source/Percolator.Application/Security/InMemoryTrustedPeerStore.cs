using Percolator.Identity;
using System.Collections.Concurrent;

namespace Percolator.Application.Security
{
    public class InMemoryTrustedPeerStore : ITrustedPeerStore
    {
        private readonly ConcurrentDictionary<string, byte> _trustedThumbprints = new(StringComparer.OrdinalIgnoreCase);

        public InMemoryTrustedPeerStore(IIdentityService identityService)
        {
            // A node must always trust its own certificate. The first available identity is used.
            var selfIdentityName = identityService.ListIdentityNames().FirstOrDefault();
            if (selfIdentityName is not null)
            {
                var selfCertificate = identityService.GetIdentityCertificate(selfIdentityName);
                Add(selfCertificate.Thumbprint);
            }
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
