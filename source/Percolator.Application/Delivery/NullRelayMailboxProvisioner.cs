using System.Threading;
using System.Threading.Tasks;
using Percolator.Application2.Delivery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Delivery;

/// <summary>
/// Placeholder <see cref="IRelayMailboxProvisioner"/> used until the gRPC provisioner is wired.
/// All operations report <c>PROVISIONER_UNAVAILABLE</c> so callers fail deterministically.
/// </summary>
public sealed class NullRelayMailboxProvisioner : IRelayMailboxProvisioner
{
    private static DomainResult Unavailable() => DomainResult.Failure(
        new DomainError("PROVISIONER_UNAVAILABLE", "No relay mailbox provisioner is configured."));

    public Task<DomainResult> ProvisionMailboxAsync(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId relayIdentityId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default)
        => Task.FromResult(Unavailable());

    public Task<DomainResult> PublishPreKeyBundleAsync(
        PublicIdentityId ownerIdentityId,
        DeviceId deviceId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        int newOneTimePreKeyCount = 25,
        CancellationToken ct = default)
        => Task.FromResult(Unavailable());

    public Task<DomainResult<int>> DrainMailboxOnceAsync(
        PublicIdentityId ownerIdentityId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default)
        => Task.FromResult(DomainResult<int>.Failure(
            new DomainError("PROVISIONER_UNAVAILABLE", "No relay mailbox provisioner is configured.")));

    public Task StopMailboxStreamAsync(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId relayIdentityId,
        CancellationToken ct = default)
        => Task.CompletedTask;
}
