using Percolator.Application.Chat;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.Application.Apps.Chat;

/// <summary>
/// Service for resolving blinded routing tokens (PKH bytes) to local PeerId identities.
/// </summary>
public sealed class RelayTargetResolver : IRelayTargetResolver
{
    private readonly IPeerIdentityQueries _peerIdentityQueries;
    private readonly IPeerIdentityRepository _peerIdentityRepository;

    public RelayTargetResolver(
        IPeerIdentityQueries peerIdentityQueries,
        IPeerIdentityRepository peerIdentityRepository)
    {
        _peerIdentityQueries = peerIdentityQueries;
        _peerIdentityRepository = peerIdentityRepository;
    }

    public async Task<List<PeerId>> ResolveTargetsAsync(List<IdentityPublicKeyHash> destinationPkhBytes, CancellationToken ct = default)
    {
        var peerIds = new List<PeerId>();

        foreach (var pkhBytes in destinationPkhBytes)
        {
            var peerId = await _peerIdentityQueries.GetPeerIdByPkhAsync(pkhBytes, ct);

            if (peerId == null)
            {
                // Create a new placeholder PeerIdentity aggregate
                var newPeerIdentity = new PeerIdentity(
                    name: $"Unknown_{Guid.NewGuid():N}",
                    publicKeyHash: pkhBytes.Span.ToArray());
                
                await _peerIdentityRepository.SaveAsync(newPeerIdentity, ct);
                peerIds.Add(newPeerIdentity.PeerId);
            }
            else
            {
                peerIds.Add(peerId);
            }
        }

        return peerIds;
    }
}
