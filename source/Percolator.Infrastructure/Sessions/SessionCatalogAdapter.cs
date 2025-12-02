using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Percolator.Cryptography;

namespace Percolator.Infrastructure.Sessions;

public sealed class SessionCatalogAdapter : ISessionCatalog
{
    private readonly ISessionRepository _sessions;

    public SessionCatalogAdapter(ISessionRepository sessions)
    {
        _sessions = sessions;
    }

    public async IAsyncEnumerable<SessionId> EnumerateActiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = await _sessions.GetAllActiveAsync(cancellationToken).ConfigureAwait(false);
        foreach (var s in list)
            yield return s.Id;
    }
}
