using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat;

public sealed class PkhPeerResolver : IPkhPeerResolver
{
    private readonly IPeerPublicSigningKeyStore _store;

    public PkhPeerResolver(IPeerPublicSigningKeyStore store)
    {
        _store = store;
    }

    public async Task<ParticipantId?> GetParticipantIdByPkhAsync(Pkh pkh, CancellationToken cancellationToken)
    {
        var identityPublicKeyHash = IdentityPublicKeyHash.FromBytesOwned(pkh.Value);
        var peerId = await _store.GetPeerIdByPublicKeyHashAsync(identityPublicKeyHash, cancellationToken).ConfigureAwait(false);
        return peerId is null ? null : new ParticipantId(peerId.Value);
    }
}
