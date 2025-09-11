using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Apps.Chat;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat
{
    public class SqliteGroupManagerStateStore : IGroupManagerStateStore
    {
        private readonly PercolatorDbContext _db;
        public SqliteGroupManagerStateStore(PercolatorDbContext db) { _db = db; }

        public async Task<byte[]?> GetAsync(Guid conversationId, CancellationToken ct)
        {
            var row = await _db.GroupManagerStates.AsNoTracking().FirstOrDefaultAsync(x => x.ConversationId == conversationId, ct);
            return row?.StateBlob;
        }

        public async Task SaveAsync(Guid conversationId, byte[] stateBlob, DateTimeOffset updatedAtUtc, CancellationToken ct)
        {
            var row = await _db.GroupManagerStates.FirstOrDefaultAsync(x => x.ConversationId == conversationId, ct);
            if (row is null)
            {
                row = new GroupManagerStateDbo
                {
                    ConversationId = conversationId,
                    StateBlob = stateBlob,
                    UpdatedAtUtc = updatedAtUtc
                };
                _db.GroupManagerStates.Add(row);
            }
            else
            {
                row.StateBlob = stateBlob;
                row.UpdatedAtUtc = updatedAtUtc;
            }

            await _db.SaveChangesAsync(ct);
        }
    }
}
