using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Apps.Chat;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat
{
    public sealed class SqliteDirectSessionConversationLookup : IDirectSessionConversationLookup
    {
        private readonly PercolatorDbContext _db;
        public SqliteDirectSessionConversationLookup(PercolatorDbContext db)
        {
            _db = db;
        }

        public async Task<Guid?> GetDirectSessionIdAsync(Guid conversationId, int selfIdentityId, CancellationToken ct)
        {
            var row = await _db.DirectSessionConversations
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.SelfIdentityId == selfIdentityId, ct);
            if (row is null) return null;
            return row.DirectSessionId;
        }

        public Task<Guid?> GetDirectSessionIdAsync(Guid conversationId, int selfIdentityId, Guid remotePeerId, CancellationToken ct)
        {
            // Current schema does not include remotePeerId in the mapping; delegate to existing lookup.
            return GetDirectSessionIdAsync(conversationId, selfIdentityId, ct);
        }
    }
}
