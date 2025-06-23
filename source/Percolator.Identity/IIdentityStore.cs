using Percolator.Identity.Model;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Identity;

public interface IIdentityStore
{
    Task StoreIdentityAsync(IdentityRecord identity, CancellationToken cancellationToken = default);
    Task<IdentityRecord?> GetIdentityAsync(string identityName, CancellationToken cancellationToken = default);
    Task<IEnumerable<string>> ListIdentityNamesAsync(CancellationToken cancellationToken = default);
    Task<bool> IdentityExistsAsync(string identityName, CancellationToken cancellationToken = default);
}
