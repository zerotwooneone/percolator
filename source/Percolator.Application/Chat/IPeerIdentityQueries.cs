using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Chat;

public interface IPeerIdentityQueries
{
    Task<RatchetIdentityKey?> GetPublicKeyByPkhAsync(IdentityPublicKeyHash senderPkh, CancellationToken ct);
    Task<PeerId?> GetPeerIdByPkhAsync(IdentityPublicKeyHash pkh, CancellationToken ct);
    Task<Pkh?> GetPublicKeyHashAsync(PeerId peerId, CancellationToken cancellationToken);
    Task<PublicIdentityId?> GetPublicIdentityIdAsync(PeerId senderPeerId, CancellationToken cancellationToken);
}
