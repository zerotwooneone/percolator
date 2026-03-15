using Microsoft.EntityFrameworkCore;
using Percolator.Chat.App;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat
{
    public class SqliteGroupAdminStateStore : IGroupAdminStateStore
    {
        private readonly PercolatorDbContext _db;
        public SqliteGroupAdminStateStore(PercolatorDbContext db) { _db = db; }

        public async Task<GroupAdminState?> GetAsync(Guid conversationId, CancellationToken ct)
        {
            var row = await _db.GroupAdminStates.AsNoTracking().FirstOrDefaultAsync(x => x.ConversationId == conversationId, ct);
            if (row is null) return null;
            return new GroupAdminState(row.NextAdminSequenceNumber, row.LastCommittedKeyVersion);
        }

        public async Task InitializeIfMissingAsync(Guid conversationId, CancellationToken ct)
        {
            var exists = await _db.GroupAdminStates.AnyAsync(x => x.ConversationId == conversationId, ct);
            if (exists) return;
            _db.GroupAdminStates.Add(new GroupAdminStateDbo
            {
                ConversationId = conversationId,
                NextAdminSequenceNumber = 1UL,
                LastCommittedKeyVersion = 0u
            });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<bool> TryCommitAsync(Guid conversationId, ulong expectedAdminSequenceNumber, uint committedKeyVersion, CancellationToken ct)
        {
            // Enforce first-commit-wins and monotonic key versions: update with concurrency check
            var row = await _db.GroupAdminStates.FirstOrDefaultAsync(x => x.ConversationId == conversationId, ct);
            if (row is null)
            {
                // initialize then retry once
                await InitializeIfMissingAsync(conversationId, ct);
                row = await _db.GroupAdminStates.FirstAsync(x => x.ConversationId == conversationId, ct);
            }

            if (row.NextAdminSequenceNumber != expectedAdminSequenceNumber)
            {
                return false; // another commit already advanced sequence
            }

            // Key version continuity: next must be exactly last+1
            if (committedKeyVersion != row.LastCommittedKeyVersion + 1)
            {
                return false;
            }

            row.NextAdminSequenceNumber = expectedAdminSequenceNumber + 1;
            row.LastCommittedKeyVersion = committedKeyVersion;
            await _db.SaveChangesAsync(ct);
            return true;
        }
    }
}
