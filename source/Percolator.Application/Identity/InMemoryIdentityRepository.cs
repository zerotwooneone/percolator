using Percolator.Identity.Model;
using Percolator.Network;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Percolator.Application.Identity;

public class InMemoryIdentityRepository : IIdentityRepository
{
    private readonly ConcurrentDictionary<PublicKeyHash, IdentityRecord> _identities = new();

    public Task<IdentityRecord?> GetIdentityForPublicKeyAsync(PublicKeyHash publicKeyHash)
    {
        _identities.TryGetValue(publicKeyHash, out var identity);
        return Task.FromResult(identity);
    }

    public Task AssociatePublicKeyWithIdentityAsync(PublicKeyHash publicKeyHash, IdentityRecord identity)
    {
        _identities[publicKeyHash] = identity;
        return Task.CompletedTask;
    }
}
