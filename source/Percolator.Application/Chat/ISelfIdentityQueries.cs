using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Chat;

public interface ISelfIdentityQueries
{
    Task<RatchetIdentityKey?> GetActiveIdentityFingerprintAsync(CancellationToken ct);
    Task<ZkServerSecretParamsSeedBytes?> GetZkServerSecretParamsSeedAsync(CancellationToken ct);

    Task<(IdentityPublicKeyHash PublicKeyHash, PublicIdentityId PublicIdentityId)?> GetIdentityParticipantInfoAsync(
        SelfId selfIdentityId, CancellationToken ct);
    Task<PublicIdentityId?> GetSelfIdentityPublicKeyAsync(SelfId selfIdentityId, CancellationToken ct);
}
