using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Sessions;

internal sealed class RatchetKeyIndexAdapter : IRatchetKeyIndex
{
    private readonly PercolatorDbContext _db;

    public RatchetKeyIndexAdapter(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<SessionId?> TryResolveAsync(CryptoSelfId selfIdentityId, RatchetEphemeralKey headerPublicKey, CancellationToken cancellationToken = default)
    {
        var row = await _db.RatchetKeyIndex
            .AsNoTracking()
            // EF Core can translate byte[] equality to BLOB comparison for SQLite
            .Where(r => r.SelfIdentityId == selfIdentityId.Value && r.RatchetPublicKey == headerPublicKey.ToArray())
            .Select(r => new { r.DirectSessionId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null ? (SessionId?)null : new SessionId(row.DirectSessionId);
    }

    public async Task UpsertAsync(CryptoSelfId selfIdentityId, SessionId sessionId, RatchetEphemeralKey headerPublicKey, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        var set = _db.RatchetKeyIndex;

        var existing = await set
            // EF Core can translate byte[] equality to BLOB comparison for SQLite
            .Where(r => r.RatchetPublicKey == headerPublicKey.ToArray())
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            await set.AddAsync(new RatchetKeyIndexDbo
            {
                SelfIdentityId = selfIdentityId.Value,
                DirectSessionId = sessionId.Value,
                RatchetPublicKey = headerPublicKey.ToArray(),
                UpdatedAtUtc = updatedAtUtc
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            existing.DirectSessionId = sessionId.Value;
            existing.UpdatedAtUtc = updatedAtUtc;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
