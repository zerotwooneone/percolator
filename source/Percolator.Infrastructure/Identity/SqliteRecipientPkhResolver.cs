using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Apps.Chat;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity
{
    public sealed class SqliteRecipientPkhResolver : IRecipientPkhResolver
    {
        private readonly PercolatorDbContext _db;
        public SqliteRecipientPkhResolver(PercolatorDbContext db)
        {
            _db = db;
        }

        public async Task<byte[]?> GetActivePkhAsync(Guid peerId, CancellationToken ct)
        {
            var row = await _db.PeerPublicSigningKeys
                .AsNoTracking()
                .Where(x => x.PeerId.Value == peerId && x.ExpiredAtUtc == null)
                .OrderByDescending(x => x.ActiveAtUtc)
                .FirstOrDefaultAsync(ct);
            return row?.PublicKeyHash;
        }
    }
}
