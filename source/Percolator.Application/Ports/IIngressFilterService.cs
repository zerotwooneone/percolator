using Percolator.Domain.Common;
using Percolator.Domain.Identities;

namespace Percolator.Application.Ports;

public interface IIngressFilterService
{
    ValueTask<DomainResult> CheckIngressAllowedAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        CancellationToken ct = default);
}
