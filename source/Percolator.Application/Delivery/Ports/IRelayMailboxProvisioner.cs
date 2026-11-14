using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Delivery.Ports;

public sealed record MailboxTokenTriad(
    BlindedRoutingToken RoutingToken,
    DeliveryToken DeliveryToken,
    DeliveryToken DrainToken,
    DeliveryToken? PreviousDrainToken = null);

/// <summary>
/// Relay-side hosted pre-key bundle snapshot for maintenance decisions.
/// <paramref name="Found"/> is false when no hosted bundle exists for the routing
/// token + device (distinct from a found bundle that simply has 0 OPKs).
/// </summary>
public sealed record RelayPreKeyStatus(
    bool Found,
    uint OneTimePreKeyCount,
    SignedPreKeyId SignedPreKeyId);

/// <summary>
/// Mailbox registration and hosted pre-key publication against a relay. Envelope
/// retrieval lives on <see cref="IRelayMailboxClient"/> — a provisioner performs
/// one-shot, drain-token-authenticated writes only.
/// </summary>
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
        int newOneTimePreKeyCount = 100,
        CancellationToken ct = default);

    Task<DomainResult<RelayPreKeyStatus>> GetPreKeyStatusAsync(
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        DeviceId deviceId,
        CancellationToken ct = default);
}
