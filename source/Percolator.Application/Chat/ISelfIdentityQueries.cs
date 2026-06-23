using Percolator.Chat.GroupLedger;
using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public interface ISelfIdentityQueries
{
    Task<RatchetIdentityKey?> GetActiveIdentityFingerprintAsync(CancellationToken ct);
    Task<ZkServerSecretParamsSeedBytes?> GetZkServerSecretParamsSeedAsync(CancellationToken ct);
}
