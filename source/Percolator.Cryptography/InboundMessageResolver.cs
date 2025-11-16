using System;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Cryptography;

public sealed class InboundMessageResolver
{
    private readonly IRatchetKeyIndex _index;
    private readonly ISessionCatalog _catalog;
    private readonly ISessionRepository _repo;

    public InboundMessageResolver(IRatchetKeyIndex index, ISessionCatalog catalog, ISessionRepository repo)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    public async Task<(SessionId sessionId, Plaintext plaintext)?> ResolveAsync(
        SessionRatchetMessage message,
        IClock clock,
        CancellationToken cancellationToken = default)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        if (clock is null) throw new ArgumentNullException(nameof(clock));

        var header = message.GetHeader();

        // Fast path: index lookup
        var resolved = await _index.TryResolveAsync(header.PreKey, cancellationToken).ConfigureAwait(false);
        if (resolved is SessionId sid)
        {
            var session = await _repo.GetAsync(sid, cancellationToken).ConfigureAwait(false);
            if (session is not null)
            {
                var pt = session.Decrypt(message, clock);
                await _repo.UpdateAsync(session, cancellationToken).ConfigureAwait(false);
                await _index.UpsertAsync(sid, header.PreKey, clock.UtcNow, cancellationToken).ConfigureAwait(false);
                return (sid, pt);
            }
        }

        // Slow path: enumerate sessions and try decrypt until one succeeds
        await foreach (var candidate in _catalog.EnumerateActiveAsync(cancellationToken).ConfigureAwait(false))
        {
            var s = await _repo.GetAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (s is null) continue;
            try
            {
                var pt = s.Decrypt(message, clock);
                await _repo.UpdateAsync(s, cancellationToken).ConfigureAwait(false);
                await _index.UpsertAsync(candidate, header.PreKey, clock.UtcNow, cancellationToken).ConfigureAwait(false);
                return (candidate, pt);
            }
            catch
            {
                // Not a match; continue
            }
        }

        return null;
    }
}
