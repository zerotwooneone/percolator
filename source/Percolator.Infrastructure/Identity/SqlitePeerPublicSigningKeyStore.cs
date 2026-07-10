using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity;

public class SqlitePeerPublicSigningKeyStore : IPeerPublicSigningKeyStore
{
    private readonly PercolatorDbContext _db;

    public SqlitePeerPublicSigningKeyStore(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task ActivateIfChangedAsync(PeerId peerId, byte[] publicKeySpki, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Check if any row exists with this public key (for any peer). If so, avoid inserting a duplicate.
            var anyByKey = await _db.PeerPublicSigningKeys
                .Where(x => x.PublicKey.SequenceEqual(publicKeySpki))
                .FirstOrDefaultAsync(ct);
            if (anyByKey is not null)
            {
                if (anyByKey.PeerId == peerId.Value)
                {
                    // Same peer: ensure it's active
                    if (anyByKey.ExpiredAtUtc is null)
                    {
                        await tx.CommitAsync(ct);
                        return;
                    }
                    await _db.PeerPublicSigningKeys
                        .Where(x => x.Id == anyByKey.Id)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.ExpiredAtUtc, (DateTimeOffset?)null)
                            .SetProperty(x => x.ActiveAtUtc, nowUtc)
                            .SetProperty(x => x.PublicKey, publicKeySpki), ct);
                    await tx.CommitAsync(ct);
                    return;
                }

                // Different peer already owns this key. Treat as idempotent no-op to respect uniqueness.
                await tx.CommitAsync(ct);
                return;
            }

            // SQLite provider cannot translate ORDER BY over DateTimeOffset here reliably.
            // Materialize then order in-memory to get the latest active row.
            var activeRows = await _db.PeerPublicSigningKeys
                .Where(x => x.PeerId == peerId.Value && x.ExpiredAtUtc == null)
                .AsNoTracking()
                .ToListAsync(ct);
            var active = activeRows
                .OrderByDescending(x => x.ActiveAtUtc)
                .FirstOrDefault();

            if (active is not null && active.PublicKey.SequenceEqual(publicKeySpki))
            {
                // Idempotent: same key already active -> no-op
                await tx.CommitAsync(ct);
                return;
            }

            if (active is not null)
            {
                await _db.PeerPublicSigningKeys
                    .Where(x => x.Id == active.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.ExpiredAtUtc, nowUtc), ct);
            }

            var dbo = new PeerPublicSigningKeyDbo
            {
                PeerId = peerId.Value,
                PublicKey = publicKeySpki,
                ActiveAtUtc = nowUtc,
                ExpiredAtUtc = null
            };
            _db.PeerPublicSigningKeys.Add(dbo);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<PeerId?> GetPeerIdByPublicIdentityIdAsync(PublicIdentityId publicIdentityId, CancellationToken ct = default)
    {
        var publicIdentityIdBytes = publicIdentityId.Value.ToByteArray();
        var peerIdentity = await _db.PeerIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PublicIdentityId == publicIdentityId.Value, ct);
        return peerIdentity is null ? null : new PeerId(peerIdentity.PeerId);
    }
}
