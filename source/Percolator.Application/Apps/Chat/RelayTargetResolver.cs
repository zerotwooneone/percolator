using Percolator.Application.Chat;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat;

/// <summary>
/// Service for resolving blinded routing tokens (PKH bytes) to local PeerId identities.
/// </summary>
public sealed class RelayTargetResolver : IRelayTargetResolver
{
    private readonly IPeerIdentityRepository _peerIdentityRepository;

    public RelayTargetResolver(IPeerIdentityRepository peerIdentityRepository)
    {
        _peerIdentityRepository = peerIdentityRepository;
    }

    public async Task<List<PeerId>> ResolveTargetsAsync(List<byte[]> destinationPkhBytes, CancellationToken ct = default)
    {
        var peerIds = new List<PeerId>();

        foreach (var pkhBytes in destinationPkhBytes)
        {
            var peerIdentity = await _peerIdentityRepository.FindByPublicKeyHashAsync(pkhBytes, ct);

            if (peerIdentity == null)
            {
                // Create a new placeholder PeerIdentity aggregate
                var newPeerIdentity = new PeerIdentity(
                    name: $"Unknown_{Guid.NewGuid():N}",
                    publicKeyHash: pkhBytes);
                
                await _peerIdentityRepository.SaveAsync(newPeerIdentity, ct);
                peerIds.Add(newPeerIdentity.PeerId);
            }
            else
            {
                peerIds.Add(peerIdentity.PeerId);
            }
        }

        return peerIds;
    }
}
