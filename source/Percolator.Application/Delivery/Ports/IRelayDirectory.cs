using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Delivery.Ports;

public sealed record RelayDirectoryEntry(
    PublicIdentityId RelayIdentityId,
    string DisplayName,
    RelayEndpoint Endpoint,
    bool IsLocalRelayHost,
    IReadOnlyList<PublicIdentityId> LinkedOwnerIdentityIds);

public interface IRelayDirectory
{
    Task<IReadOnlyList<RelayDirectoryEntry>> ListAsync(CancellationToken ct = default);
    Task<DomainResult<RelayDirectoryEntry>> CreateLocalRelayHostAsync(
        PublicIdentityId relayIdentityId,
        string displayName,
        RelayEndpoint endpoint,
        IEnumerable<PublicIdentityId> initialLinkedOwnerIds,
        CancellationToken ct = default);
    Task<DomainResult<RelayDirectoryEntry>> RegisterRemoteRelayAsync(
        PublicIdentityId relayIdentityId,
        RelayEndpoint endpoint,
        CertificatePin? pinnedCertificate = null,
        CancellationToken ct = default);
    Task<DomainResult<RelayMailboxBinding>> LinkIdentityAsync(
        PublicIdentityId relayIdentityId,
        PublicIdentityId ownerIdentityId,
        BlindedRoutingToken? routingToken = null,
        DeliveryToken? drainToken = null,
        CancellationToken ct = default);
    Task<DomainResult> UnlinkIdentityAsync(
        PublicIdentityId relayIdentityId,
        PublicIdentityId ownerIdentityId,
        CancellationToken ct = default);
    Task<DomainResult<MailboxTokenTriad>> GetMailboxTokensAsync(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId relayIdentityId,
        CancellationToken ct = default);
    Task<DomainResult> ProvisionLinkedMailboxesAsync(
        PublicIdentityId ownerIdentityId,
        CancellationToken ct = default);
}
