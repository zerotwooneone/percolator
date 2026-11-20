using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Delivery.Ports;

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
        uint newOneTimePreKeyCount,
        CancellationToken ct = default);

    Task<DomainResult<RelayPreKeyStatus>> GetPreKeyStatusAsync(
        DeviceId deviceId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default);
}
