using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Ports;

public interface IRatchetSessionRepository
{
    Task<DirectRatchetSession?> GetSessionAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        CancellationToken ct = default);

    Task SaveSessionAsync(DirectRatchetSession session, CancellationToken ct = default);
}
