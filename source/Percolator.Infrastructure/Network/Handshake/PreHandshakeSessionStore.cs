using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Network.Handshake;
using Percolator.Infrastructure.Persistence;
using System.Security.Cryptography;

namespace Percolator.Infrastructure.Network.Handshake
{
    internal sealed class PreHandshakeSessionStore : IPreHandshakeSessionStore
    {
        private readonly PercolatorDbContext _db;

        public PreHandshakeSessionStore(PercolatorDbContext db)
        {
            _db = db;
        }

        public async Task<PreHandshakeRecord?> TryGetMostRecentAsync(int selfIdentityId, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var x = await _db.PreHandshakeSessions
                .AsNoTracking()
                .Where(r => r.SelfIdentityId == selfIdentityId && (r.ExpiresAtUtc == null || r.ExpiresAtUtc > now))
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (x is null) return null;
            return new PreHandshakeRecord(
                Id: x.Id,
                SelfIdentityId: x.SelfIdentityId,
                RecipientPublicKeyHash: x.RecipientPublicKeyHash ?? Array.Empty<byte>(),
                LocalRequestId: x.LocalRequestId,
                InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
                InitialRootKey: x.InitialRootKey,
                CreatedAtUtc: x.CreatedAtUtc,
                ExpiresAtUtc: x.ExpiresAtUtc,
                RemoteIdentityKeySpki: x.RemoteIdentityKeySpki
            );
        }

        public async Task SaveAsync(PreHandshakeRecord record, CancellationToken cancellationToken)
        {
            // Compute SPKI hash for indexing if provided
            byte[]? spkiHash = null;
            if (record.RemoteIdentityKeySpki is { Length: > 0 })
            {
                using var sha = SHA256.Create();
                spkiHash = sha.ComputeHash(record.RemoteIdentityKeySpki);
            }
            var dbo = new PreHandshakeSessionDbo
            {
                SelfIdentityId = record.SelfIdentityId,
                RecipientPublicKeyHash = record.RecipientPublicKeyHash,
                LocalRequestId = record.LocalRequestId,
                InitialRootKey = record.InitialRootKey,
                CreatedAtUtc = record.CreatedAtUtc,
                ExpiresAtUtc = record.ExpiresAtUtc,
                RemoteIdentityKeySpki = record.RemoteIdentityKeySpki,
                RemoteIdentityKeySpkiHash = spkiHash,
            };
            // Note: Id is ValueGeneratedOnAdd; do not set it here so SQLite AUTOINCREMENT assigns it.
            _db.PreHandshakeSessions.Add(dbo);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<PreHandshakeRecord> EnumeratePendingAsync(int selfIdentityId, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var query = _db.PreHandshakeSessions
                .AsNoTracking()
                .Where(x => x.SelfIdentityId == selfIdentityId && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now))
                .OrderByDescending(x => x.CreatedAtUtc)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken);

            await foreach (var x in query)
            {
                yield return new PreHandshakeRecord(
                    Id: x.Id,
                    SelfIdentityId: x.SelfIdentityId,
                    RecipientPublicKeyHash: x.RecipientPublicKeyHash ?? Array.Empty<byte>(),
                    LocalRequestId: x.LocalRequestId,
                    // Initiator ephemeral private key is no longer persisted
                    InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
                    InitialRootKey: x.InitialRootKey,
                    CreatedAtUtc: x.CreatedAtUtc,
                    ExpiresAtUtc: x.ExpiresAtUtc,
                    RemoteIdentityKeySpki: x.RemoteIdentityKeySpki
                );
            }
        }

        public async Task DeleteAsync(long recordId, int selfIdentityId, CancellationToken cancellationToken)
        {
            var entity = await _db.PreHandshakeSessions
                .Where(x => x.Id == recordId && x.SelfIdentityId == selfIdentityId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (entity != null)
            {
                _db.PreHandshakeSessions.Remove(entity);
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task PurgeExpiredAsync(int selfIdentityId, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var expired = await _db.PreHandshakeSessions
                .Where(x => x.SelfIdentityId == selfIdentityId && x.ExpiresAtUtc != null && x.ExpiresAtUtc <= now)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (expired.Count > 0)
            {
                _db.PreHandshakeSessions.RemoveRange(expired);
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
