namespace Percolator.Domain.Delivery.Ports;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

public sealed record RelayDirectoryEntry(
    PublicIdentityId RelayIdentityId,
    string DisplayName,
    RelayEndpoint Endpoint,
    bool IsLocalRelayHost,
    IReadOnlyList<PublicIdentityId> LinkedOwnerIdentityIds,
    IReadOnlyList<PublicIdentityId>? EnabledOwnerIdentityIds = null)
{
    public IReadOnlyList<PublicIdentityId> EnabledOwnerIdentityIds { get; init; } =
        EnabledOwnerIdentityIds ?? LinkedOwnerIdentityIds;
}

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
    Task<DomainResult> SetMailboxBindingStatusAsync(
        PublicIdentityId relayIdentityId,
        PublicIdentityId ownerIdentityId,
        RelayLinkStatus status,
        CancellationToken ct = default);
    Task<DomainResult<MailboxTokenTriad>> GetMailboxTokensAsync(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId relayIdentityId,
        CancellationToken ct = default);
    /// <summary>
    /// Provision-on-connect: ensures the owner's mailbox exists relay-side, stamping
    /// the binding's provisioned timestamp. Idempotent — skips the provisioning RPC
    /// while the binding's mark is inside the freshness window.
    /// </summary>
    Task<DomainResult> EnsureMailboxProvisionedAsync(
        PublicIdentityId relayIdentityId,
        PublicIdentityId ownerIdentityId,
        CancellationToken ct = default);
    Task<DomainResult> ProvisionLinkedMailboxesAsync(
        PublicIdentityId ownerIdentityId,
        CancellationToken ct = default);
}
