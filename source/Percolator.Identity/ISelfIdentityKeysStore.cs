using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Identity;

public interface ISelfIdentityKeysStore
{
    Task<X3dhKeys?> LoadAsync(int selfIdentityId, CancellationToken cancellationToken = default);
    Task SaveAsync(int selfIdentityId, X3dhKeys keys, CancellationToken cancellationToken = default);
}
