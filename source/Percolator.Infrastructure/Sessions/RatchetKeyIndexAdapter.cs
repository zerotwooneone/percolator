using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography;
using Percolator.Infrastructure.Persistence;
using Percolator.Application.Identity;

namespace Percolator.Infrastructure.Sessions;

internal sealed class RatchetKeyIndexAdapter : IRatchetKeyIndex
{
    private readonly PercolatorDbContext _db;
    private readonly ActiveIdentityContext _active;

    public RatchetKeyIndexAdapter(PercolatorDbContext db, ActiveIdentityContext active)
    {
        _db = db;
        _active = active;
    }

    public async Task<SessionId?> TryResolveAsync(RatchetEphemeralKey headerPublicKey, CancellationToken cancellationToken = default)
    {
        var selfIdentityId = _active.Identity!.SelfIdentityId;
        var row = await _db.RatchetKeyIndex
            .AsNoTracking()
            // EF Core can translate byte[] equality to BLOB comparison for SQLite
            .Where(r => r.SelfIdentityId == selfIdentityId && r.RatchetPublicKey == headerPublicKey.Value)
            .Select(r => new { r.DirectSessionId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null ? (SessionId?)null : new SessionId(row.DirectSessionId);
    }

    public async Task UpsertAsync(SessionId sessionId, RatchetEphemeralKey headerPublicKey, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        var set = _db.RatchetKeyIndex;
        // Scope by current active self identity
        var selfIdentityId = _active.Identity!.SelfIdentityId;

        var existing = await set
            // EF Core can translate byte[] equality to BLOB comparison for SQLite
            .Where(r => r.RatchetPublicKey == headerPublicKey.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            await set.AddAsync(new RatchetKeyIndexDbo
            {
                SelfIdentityId = selfIdentityId,
                DirectSessionId = sessionId.Value,
                RatchetPublicKey = headerPublicKey.Value,
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
