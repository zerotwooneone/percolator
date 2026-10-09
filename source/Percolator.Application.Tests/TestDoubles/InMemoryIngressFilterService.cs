using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class InMemoryIngressFilterService : IIngressFilterService
{
    private readonly HashSet<PublicIdentityId> _blockedIdentities = [];

    public void BlockIdentity(PublicIdentityId id) => _blockedIdentities.Add(id);
    public void UnblockIdentity(PublicIdentityId id) => _blockedIdentities.Remove(id);

    public ValueTask<DomainResult> CheckIngressAllowedAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        CancellationToken ct = default)
    {
        if (_blockedIdentities.Contains(senderId))
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError("IDENTITY_BLOCKED", "Sender is blocked or disabled.")));
        }

        return ValueTask.FromResult(DomainResult.Success());
    }
}
