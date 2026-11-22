namespace Percolator.Application.Tests.TestDoubles;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

public sealed class FakeRelayDirectory : IRelayDirectory
{
    public List<RelayDirectoryEntry> Entries { get; } = new();
    public int TokenCalls { get; private set; }

    public void AddEntry(RelayDirectoryEntry entry)
    {
        lock (Entries) { Entries.Add(entry); }
    }

    public void ClearEntries()
    {
        lock (Entries) { Entries.Clear(); }
    }

    public Task<IReadOnlyList<RelayDirectoryEntry>> ListAsync(CancellationToken ct = default)
    {
        lock (Entries)
        {
            IReadOnlyList<RelayDirectoryEntry> snapshot = Entries.ToList();
            return Task.FromResult(snapshot);
        }
    }

    public Task<DomainResult<MailboxTokenTriad>> GetMailboxTokensAsync(
        PublicIdentityId ownerIdentityId, PublicIdentityId relayIdentityId, CancellationToken ct = default)
    {
        TokenCalls++;
        var tokens = new MailboxTokenTriad(
            BlindedRoutingToken.New(), DeliveryToken.GenerateRandom(), DeliveryToken.GenerateRandom());
        return Task.FromResult(DomainResult<MailboxTokenTriad>.Success(tokens));
    }

    public Task<DomainResult> EnsureMailboxProvisionedAsync(
        PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, CancellationToken ct = default)
    {
        return Task.FromResult(DomainResult.Success());
    }

    public Task<DomainResult<RelayDirectoryEntry>> CreateLocalRelayHostAsync(
        PublicIdentityId relayIdentityId, string displayName, RelayEndpoint endpoint,
        IEnumerable<PublicIdentityId> initialLinkedOwnerIds, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<DomainResult<RelayDirectoryEntry>> RegisterRemoteRelayAsync(
        PublicIdentityId relayIdentityId, RelayEndpoint endpoint, CertificatePin? pinnedCertificate = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<DomainResult<RelayMailboxBinding>> LinkIdentityAsync(
        PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId,
        BlindedRoutingToken? routingToken = null, DeliveryToken? drainToken = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<DomainResult> UnlinkIdentityAsync(
        PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<DomainResult> SetMailboxBindingStatusAsync(
        PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, RelayLinkStatus status,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<DomainResult> ProvisionLinkedMailboxesAsync(
        PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
