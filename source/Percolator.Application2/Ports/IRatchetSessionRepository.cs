using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Ports;

public interface IRatchetSessionRepository
{
    Task<DirectRatchetSession?> GetSessionAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        CancellationToken ct = default);

    Task SaveSessionAsync(DirectRatchetSession session, CancellationToken ct = default);
}
