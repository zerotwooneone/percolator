using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Network;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;

namespace Percolator.Infrastructure.Sessions
{
    internal sealed class RatchetKeySessionLookup : IRatchetKeySessionLookup
    {
        private readonly PercolatorDbContext _db;

        public RatchetKeySessionLookup(PercolatorDbContext db)
        {
            _db = db;
        }

        public async Task<DirectSessionId?> TryResolveAsync(byte[] ratchetPublicKey, int selfIdentityId, CancellationToken cancellationToken)
        {
            var row = await _db.Set<RatchetKeyIndexDbo>()
                .AsNoTracking()
                .Where(r => r.SelfIdentityId == selfIdentityId && r.RatchetPublicKey.SequenceEqual(ratchetPublicKey))
                .Select(r => new { r.DirectSessionId })
                .FirstOrDefaultAsync(cancellationToken);

            return row is null ? (DirectSessionId?)null : new DirectSessionId(row.DirectSessionId);
        }

        public async Task UpsertAsync(DirectSessionId sessionId, int selfIdentityId, byte[] ratchetPublicKey, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken)
        {
            var set = _db.Set<RatchetKeyIndexDbo>();
            var existing = await set
                .Where(r => r.SelfIdentityId == selfIdentityId && r.RatchetPublicKey.SequenceEqual(ratchetPublicKey))
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is null)
            {
                await set.AddAsync(new RatchetKeyIndexDbo
                {
                    SelfIdentityId = selfIdentityId,
                    DirectSessionId = sessionId.Value,
                    RatchetPublicKey = ratchetPublicKey,
                    UpdatedAtUtc = updatedAtUtc
                }, cancellationToken);
            }
            else
            {
                existing.DirectSessionId = sessionId.Value;
                existing.UpdatedAtUtc = updatedAtUtc;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
