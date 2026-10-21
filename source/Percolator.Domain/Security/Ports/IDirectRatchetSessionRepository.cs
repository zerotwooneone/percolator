using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Ports;

/// <summary>
/// Port for persisting and retrieving pairwise Double Ratchet sessions.
/// </summary>
public interface IDirectRatchetSessionRepository
{
    Task<DirectRatchetSession?> GetByIdAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    Task<DirectRatchetSession?> GetByPeerDeviceAsync(
        PublicIdentityId ownerIdentityId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(DirectRatchetSession session, CancellationToken cancellationToken = default);

    Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default);
}
