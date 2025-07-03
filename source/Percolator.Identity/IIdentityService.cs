using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Identity.Model;

namespace Percolator.Identity;

public interface IIdentityService
{
    Task<(IdentityRecord Identity, X3dhKeys Keys)> GetOrCreateIdentityAsync(string name, CancellationToken cancellationToken = default);
    Task<IdentityRecord> CreateIdentityAsync(string name, string? nickname, CancellationToken cancellationToken = default);
    Task<IdentityRecord?> GetIdentityRecordAsync(string name, CancellationToken cancellationToken = default);
    Task<IEnumerable<string>> ListIdentityNamesAsync(CancellationToken cancellationToken = default);
}
