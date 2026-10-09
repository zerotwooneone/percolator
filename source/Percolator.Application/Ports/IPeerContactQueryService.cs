using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Ports;

public sealed record PeerContactSummaryReadModel(
    PublicIdentityId IdentityId,
    string DisplayName,
    PeerTrustLevel TrustLevel,
    ContactState State,
    DateTimeOffset AddedAtUtc,
    bool IsOnline);

public sealed record PeerContactDetailReadModel(
    PublicIdentityId IdentityId,
    string DisplayName,
    PeerTrustLevel TrustLevel,
    ContactState State,
    IdentityKey PrimaryPublicKey,
    IReadOnlyList<DeviceId> RegisteredDevices,
    DateTimeOffset AddedAtUtc,
    DateTimeOffset LastSeenAtUtc);

public interface IPeerContactQueryService
{
    Task<IReadOnlyList<PeerContactSummaryReadModel>> GetContactSummariesAsync(PublicIdentityId ownerId, CancellationToken ct = default);
    Task<PeerContactDetailReadModel?> GetContactDetailAsync(PublicIdentityId ownerId, PublicIdentityId contactId, CancellationToken ct = default);
}
