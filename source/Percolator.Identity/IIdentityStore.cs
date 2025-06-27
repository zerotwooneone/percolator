using Percolator.Identity.Model;

namespace Percolator.Identity;

public interface IIdentityStore
{
    Task StoreIdentityAsync(IdentityRecord identity, CancellationToken cancellationToken = default);
    Task<IdentityRecord?> GetIdentityAsync(string identityName, CancellationToken cancellationToken = default);
    Task<IEnumerable<string>> ListIdentityNamesAsync(CancellationToken cancellationToken = default);
    Task<bool> IdentityExistsAsync(string identityName, CancellationToken cancellationToken = default);
}
