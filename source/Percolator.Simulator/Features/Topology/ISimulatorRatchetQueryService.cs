using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Simulator.Features.Topology;

public interface ISimulatorRatchetQueryService
{
    Task<bool> HasActiveSessionAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        CancellationToken ct = default);
}
