using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public interface IPeerIdentityQueries
{
    Task<RatchetIdentityKey?> GetPublicKeyByPkhAsync(string senderPkh, CancellationToken ct);
}
