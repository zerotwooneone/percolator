using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Chat.App;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat
{
    public class SqliteGroupAdminOpStore : IGroupAdminOpStore
    {
        private readonly PercolatorDbContext _db;
        public SqliteGroupAdminOpStore(PercolatorDbContext db) { _db = db; }

        public async Task<bool> TryAddAsync(Guid conversationId, Guid opId, DateTimeOffset appliedAtUtc, CancellationToken ct)
        {
            // Fast path: try insert and rely on unique index (ConversationId, OpId)
            var dbo = new GroupAdminOpDbo
            {
                ConversationId = conversationId,
                OpId = opId,
                AppliedAtUtc = appliedAtUtc
            };
            _db.GroupAdminOps.Add(dbo);
            try
            {
                await _db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException)
            {
                // Duplicate or other DB issue
                var exists = await _db.GroupAdminOps.AnyAsync(x => x.ConversationId == conversationId && x.OpId == opId, ct);
                if (exists) return false;
                throw;
            }
        }
    }
}
