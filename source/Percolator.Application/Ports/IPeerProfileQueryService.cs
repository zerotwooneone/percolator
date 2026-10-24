using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Ports;

/// <summary>
/// CQRS read model representing a decrypted peer profile for UI presentation and queries.
/// </summary>
public sealed record PeerProfileReadModel(
    PublicIdentityId PeerId,
    string DisplayName,
    string? StatusBio,
    bool HasAvatar,
    uint Revision,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// CQRS query service port for reading peer profiles.
/// </summary>
public interface IPeerProfileQueryService
{
    Task<PeerProfileReadModel?> GetProfileAsync(PublicIdentityId peerId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<PublicIdentityId, PeerProfileReadModel>> GetProfilesAsync(
        IEnumerable<PublicIdentityId> peerIds, CancellationToken ct = default);
}
