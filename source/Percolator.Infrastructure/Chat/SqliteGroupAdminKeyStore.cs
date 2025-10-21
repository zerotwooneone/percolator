using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat
{
    public class SqliteGroupAdminKeyStore : IGroupAdminKeyStore
    {
        private readonly PercolatorDbContext _db;
        public SqliteGroupAdminKeyStore(PercolatorDbContext db) { _db = db; }

        public async Task<IReadOnlyList<GroupAdminKeyRecord>> GetKeysAsync(Guid conversationId, CancellationToken ct)
        {
            var rows = await _db.GroupAdminKeys
                .AsNoTracking()
                .Where(x => x.ConversationId == conversationId)
                .ToListAsync(ct);
            var ordered = rows
                .OrderBy(x => x.AddedAtUtc)
                .ThenBy(x => x.Id)
                .Select(r => new GroupAdminKeyRecord(new AdminPublicKey(r.AdminPublicKeySpki), r.AddedAtUtc, r.RevokedAtUtc))
                .ToList();
            return ordered;
        }

        public async Task AddKeyAsync(Guid conversationId, AdminPublicKey publicKey, DateTimeOffset addedAtUtc, CancellationToken ct)
        {
            var dbo = new GroupAdminKeyDbo
            {
                ConversationId = conversationId,
                AdminPublicKeySpki = publicKey.Bytes,
                AddedAtUtc = addedAtUtc,
                RevokedAtUtc = null
            };
            _db.GroupAdminKeys.Add(dbo);
            await _db.SaveChangesAsync(ct);
        }

        public async Task RevokeKeyAsync(Guid conversationId, AdminPublicKey publicKey, DateTimeOffset revokedAtUtc, CancellationToken ct)
        {
            // Select most recent non-revoked matching key and set RevokedAtUtc
            var candidates = await _db.GroupAdminKeys
                .Where(x => x.ConversationId == conversationId && x.RevokedAtUtc == null && x.AdminPublicKeySpki == publicKey.Bytes)
                .ToListAsync(ct);
            var dbo = candidates
                .OrderByDescending(x => x.AddedAtUtc)
                .ThenByDescending(x => x.Id)
                .FirstOrDefault();
            if (dbo is null)
            {
                // No-op if already revoked/not found (idempotent)
                return;
            }
            dbo.RevokedAtUtc = revokedAtUtc;
            await _db.SaveChangesAsync(ct);
        }
    }
}
