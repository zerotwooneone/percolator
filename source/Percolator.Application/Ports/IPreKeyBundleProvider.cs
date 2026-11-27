using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Ports;

/// <summary>
/// Application driven port for retrieving a consumable PreKeyBundle for a remote peer,
/// either from a designated Relay endpoint or direct peer transport.
/// Implemented by network/relay adapters in Infrastructure or test harnesses.
/// </summary>
public interface IPreKeyBundleProvider
{
    Task<DomainResult<PreKeyBundle>> FetchConsumableBundleAsync(
        PublicIdentityId requesterId,
        PublicIdentityId peerId,
        DeviceId? deviceId = null,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default);

    Task<DomainResult<PreKeyBundle>> FetchAuthenticatedAsync(
        PublicIdentityId callerOwnerId,
        RelayEndpoint relayEndpoint,
        BlindedRoutingToken targetRoutingToken,
        DeviceId? deviceId = null,
        CancellationToken ct = default);
}
