using Percolator.Identity;

namespace Percolator.Application.Sessions;

public interface IPeerConnectionQueries
{
    Task<IReadOnlyList<PendingInboundSnapshot>> LoadPendingInboundAsync(SelfId selfIdentityId, CancellationToken cancellationToken = default);
}
