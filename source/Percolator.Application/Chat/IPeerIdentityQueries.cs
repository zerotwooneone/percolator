using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Chat;

public interface IPeerIdentityQueries
{
    Task<RatchetIdentityKey?> GetPublicKeyByPublicIdentityIdAsync(PublicIdentityId publicIdentityId, CancellationToken ct);
    Task<PeerId?> GetPeerIdByPublicIdentityIdAsync(PublicIdentityId publicIdentityId, CancellationToken ct);
    Task<PublicIdentityId?> GetPublicIdentityIdAsync(PeerId senderPeerId, CancellationToken cancellationToken);
}
