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

        public async Task SetActingAdminAsync(Guid conversationId, Guid opId, Guid actingAdminPeerId, CancellationToken ct)
        {
            var row = await _db.GroupAdminOps.FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.OpId == opId, ct);
            if (row is null)
            {
                // No-op if op wasn't recorded (should not happen if TryAddAsync was called earlier)
                return;
            }
            row.ActingAdminPeerId = actingAdminPeerId;
            await _db.SaveChangesAsync(ct);
        }
    }
}
