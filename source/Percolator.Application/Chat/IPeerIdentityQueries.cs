using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Chat;

public interface IPeerIdentityQueries
{
    Task<RatchetIdentityKey?> GetPublicKeyByPkhAsync(IdentityPublicKeyHash senderPkh, CancellationToken ct);
    Task<PeerId?> GetPeerIdByPkhAsync(IdentityPublicKeyHash pkh, CancellationToken ct);
}
