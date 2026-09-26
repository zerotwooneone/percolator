using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Identities.Ports;

public interface IPreKeyStore
{
    Task<PreKeyBundleState?> GetStateAsync(PublicIdentityId ownerId, DeviceId deviceId, CancellationToken cancellationToken = default);
    Task SaveStateAsync(PreKeyBundleState state, CancellationToken cancellationToken = default);
}
