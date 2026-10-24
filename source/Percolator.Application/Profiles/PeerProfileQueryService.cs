using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Profiles;

public sealed class PeerProfileQueryService : IPeerProfileQueryService
{
    private readonly IPeerProfileRepository _repository;

    public PeerProfileQueryService(IPeerProfileRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<PeerProfileReadModel?> GetProfileAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        var record = await _repository.GetByPeerIdAsync(peerId, ct);
        if (record == null)
        {
            return null;
        }

        return new PeerProfileReadModel(
            record.PeerId,
            record.DisplayName,
            record.StatusBio,
            record.AvatarReference != null,
            record.Revision,
            record.UpdatedAtUtc);
    }

    public async Task<IReadOnlyDictionary<PublicIdentityId, PeerProfileReadModel>> GetProfilesAsync(
        IEnumerable<PublicIdentityId> peerIds, CancellationToken ct = default)
    {
        var result = new Dictionary<PublicIdentityId, PeerProfileReadModel>();
        foreach (var peerId in peerIds)
        {
            var profile = await GetProfileAsync(peerId, ct);
            if (profile != null)
            {
                result[peerId] = profile;
            }
        }
        return result;
    }
}
