using Microsoft.EntityFrameworkCore;
using Percolator.Application.Cryptography;
using Percolator.Cryptography;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Persistence;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.Infrastructure.Application;

public class PendingHandshakeQueries : IPendingHandshakeQueries
{
    private readonly PercolatorDbContext _dbContext;
    private readonly IClock _clock;

    public PendingHandshakeQueries(PercolatorDbContext dbContext,
        IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }
    public async IAsyncEnumerable<PendingHandshake> EnumerateOpenAsync(CancellationToken cancellationToken=default)
    {
        var nowUtc = _clock.UtcNow;

        var sqlQuery =
            from ps in _dbContext.PendingSessions.AsNoTracking()
            where ps.State == (int)ApprovalState.AwaitingApproval
            join pi in _dbContext.PeerIdentities.AsNoTracking()
                on ps.RemotePeerId equals pi.PeerId into g
            from pi in g.DefaultIfEmpty()
            select new
            {
                Id = ps.Id,                 // primitive
                RemotePeerId = ps.RemotePeerId,
                // keep raw display name-ish field (primitive or nullable) from DB
                PeerDisplayName = pi == null ? null : pi.Name,
                CreatedAtUtc = ps.CreatedAtUtc,
                ExpiresAtUtc = ps.ExpiresAtUtc
            };

        var rows = await sqlQuery.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows
                     .Where(r => r.ExpiresAtUtc == null || r.ExpiresAtUtc > nowUtc)
                     .OrderByDescending(r => r.CreatedAtUtc))
        {
            var peerName = row.PeerDisplayName
                           ?? row.RemotePeerId.ToString()[..8];

            yield return new PendingHandshake
            {
                Id = new PendingSessionId(row.Id),
                RemotePeer = new PeerId(row.RemotePeerId),
                PeerName = peerName,
                CreatedAtUtc = row.CreatedAtUtc,
                ExpiresAtUtc = row.ExpiresAtUtc
            };
        }
    }
}