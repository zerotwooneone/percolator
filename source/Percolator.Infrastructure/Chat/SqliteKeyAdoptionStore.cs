using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat
{
    public class SqliteKeyAdoptionStore : IKeyAdoptionStore
    {
        private readonly PercolatorDbContext _db;
        public SqliteKeyAdoptionStore(PercolatorDbContext db) { _db = db; }

        public async Task AddAsync(Guid conversationId, GroupKeyVersion version, IdentityPublicKey adopterIdentityKey, DateTimeOffset sentAtUtc, byte[] signature, CancellationToken ct)
        {
            var dbo = new KeyAdoptionConfirmationDbo
            {
                ConversationId = conversationId,
                KeyVersion = (uint)version.Value,
                AdopterIdentityKey = adopterIdentityKey.Bytes,
                Signature = signature,
                SentAtUtc = sentAtUtc
            };
            _db.KeyAdoptionConfirmations.Add(dbo);
            await _db.SaveChangesAsync(ct);
        }

        public Task<int> GetCountAsync(Guid conversationId, GroupKeyVersion version, CancellationToken ct)
        {
            return _db.KeyAdoptionConfirmations
                .AsNoTracking()
                .CountAsync(x => x.ConversationId == conversationId && x.KeyVersion == (uint)version.Value, ct);
        }
    }
}
