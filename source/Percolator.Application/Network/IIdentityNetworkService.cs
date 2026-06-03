using Percolator.Identity;

namespace Percolator.Application.Network;

public interface IIdentityNetworkService
{
    Task ResolvePortContentionAsync(SelfId selfId, CancellationToken ct);
}
