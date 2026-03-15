using System.Runtime.CompilerServices;
using Percolator.Cryptography;

namespace Percolator.Infrastructure.Sessions;

public sealed class SessionCatalogAdapter : ISessionCatalog
{
    private readonly ISessionRepository _sessions;

    public SessionCatalogAdapter(ISessionRepository sessions)
    {
        _sessions = sessions;
    }

    public async IAsyncEnumerable<SessionId> EnumerateActiveAsync(int selfIdentityId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = await _sessions.GetAllActiveAsync(selfIdentityId, cancellationToken).ConfigureAwait(false);
        foreach (var s in list)
            yield return s.Id;
    }
}
