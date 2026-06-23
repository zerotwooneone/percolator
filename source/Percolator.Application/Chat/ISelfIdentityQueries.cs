using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public interface ISelfIdentityQueries
{
    Task<RatchetIdentityKey?> GetActiveIdentityFingerprintAsync(CancellationToken ct);
    Task<RatchetIdentityKey?> GetZkServerSecretParamsSeedAsync(CancellationToken ct);
}
