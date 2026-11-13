using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Delivery.Ports;

public sealed record MailboxTokenTriad(
    BlindedRoutingToken RoutingToken,
    DeliveryToken DeliveryToken,
    DeliveryToken DrainToken,
    DeliveryToken? PreviousDrainToken = null);

public interface IRelayMailboxProvisioner
{
    Task<DomainResult> ProvisionMailboxAsync(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId relayIdentityId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default);

    Task<DomainResult> PublishPreKeyBundleAsync(
        PublicIdentityId ownerIdentityId,
        DeviceId deviceId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        int newOneTimePreKeyCount = 25,
        CancellationToken ct = default);

    Task<DomainResult<int>> DrainMailboxOnceAsync(
        PublicIdentityId ownerIdentityId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default);

    Task StopMailboxStreamAsync(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId relayIdentityId,
        CancellationToken ct = default);
}
