using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Delivery.Ports;

/// <summary>
/// Port for persisting and retrieving relay-hosted pre-key bundles per identity and device.
/// </summary>
public interface IRelayPreKeyRepository
{
    Task<RelayHostedPreKeyBundle?> GetAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForIdentityAsync(
        PublicIdentityId ownerId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default);

    Task DeleteAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);
}
