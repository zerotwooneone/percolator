using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Cryptography;

public interface ISessionCatalog
{
    IAsyncEnumerable<SessionId> EnumerateActiveAsync(int selfIdentityId, CancellationToken cancellationToken = default);
}
