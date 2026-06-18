using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.Application.Chat;

public interface IPeerIdentityQueries
{
    Task<RatchetIdentityKey?> GetPublicKeyByPkhAsync(string senderPkh, CancellationToken ct);
    Task<PeerId?> GetPeerIdByPkhAsync(IdentityPublicKeyHash pkh, CancellationToken ct);
}
