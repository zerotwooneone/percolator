using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public interface ISelfIdentityQueries
{
    Task<RatchetIdentityKey?> GetActiveIdentityFingerprintAsync(CancellationToken ct);
    Task<ZkServerSecretParamsSeedBytes?> GetZkServerSecretParamsSeedAsync(CancellationToken ct);
    Task<(Pkh Pkh, Guid PeerId)?> GetIdentityParticipantInfoAsync(int selfIdentityId, CancellationToken ct);
}
