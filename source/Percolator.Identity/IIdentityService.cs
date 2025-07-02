using Percolator.Identity.Model;

namespace Percolator.Identity;

public interface IIdentityService
{
    Task<IdentityRecord> CreateIdentityAsync(string name, string? nickname, CancellationToken cancellationToken = default);
    Task<IdentityRecord?> GetIdentityRecordAsync(string name, CancellationToken cancellationToken = default);
    Task<IEnumerable<string>> ListIdentityNamesAsync(CancellationToken cancellationToken = default);
}
