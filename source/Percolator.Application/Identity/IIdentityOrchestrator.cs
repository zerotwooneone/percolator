using Percolator.Identity;

namespace Percolator.Application.Identity;

public interface IIdentityOrchestrator
{
    Task ResolveIdentityAsync(SelfId selfId,  CancellationToken cancellationToken);
}
