using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Delivery.Ports;

/// <summary>
/// Port for persisting and retrieving relay-hosted pre-key bundles per mailbox
/// (blinded routing token) and device. The relay never keys bundles by identity.
/// </summary>
public interface IRelayPreKeyRepository
{
    Task<RelayHostedPreKeyBundle?> GetAsync(
        BlindedRoutingToken routingToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForMailboxAsync(
        BlindedRoutingToken routingToken,
        CancellationToken cancellationToken = default);

    Task<bool> HasActivePreKeyAsync(
        BlindedRoutingToken routingToken,
        CancellationToken cancellationToken = default);

    Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default);

    Task DeleteAsync(
        BlindedRoutingToken routingToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);
}
