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

    public async Task ActivateIfChangedAsync(PeerId peerId, byte[] publicKeySpki, byte[] publicKeyHash, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Check if any row exists with this hash (for any peer). If so, avoid inserting a duplicate.
            var anyByHash = await _db.PeerPublicSigningKeys
                .Where(x => x.PublicKeyHash.SequenceEqual(publicKeyHash))
                .FirstOrDefaultAsync(ct);
            if (anyByHash is not null)
            {
                if (anyByHash.PeerId == peerId)
                {
                    // Same peer: ensure it's active
                    if (anyByHash.ExpiredAtUtc is null)
                    {
                        await tx.CommitAsync(ct);
                        return;
                    }
                    await _db.PeerPublicSigningKeys
                        .Where(x => x.Id == anyByHash.Id)
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
                .Where(x => x.PeerId == peerId && x.ExpiredAtUtc == null)
                .AsNoTracking()
                .ToListAsync(ct);
            var active = activeRows
                .OrderByDescending(x => x.ActiveAtUtc)
                .FirstOrDefault();

            if (active is not null && active.PublicKeyHash.SequenceEqual(publicKeyHash))
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
                PeerId = peerId,
                PublicKey = publicKeySpki,
                PublicKeyHash = publicKeyHash,
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

    public async Task<PeerId?> GetPeerIdByPublicKeyHashAsync(byte[] publicKeyHash, CancellationToken ct = default)
    {
        var row = await _db.PeerPublicSigningKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PublicKeyHash.SequenceEqual(publicKeyHash), ct);
        return row is null ? null : row.PeerId;
    }

    public async Task<byte[]?> GetPublicKeyHashByPeerIdAsync(PeerId peerId, CancellationToken ct = default)
    {
        // Materialize then order to ensure we pick the most recent active key
        var activeRows = await _db.PeerPublicSigningKeys
            .Where(x => x.PeerId == peerId && x.ExpiredAtUtc == null)
            .AsNoTracking()
            .ToListAsync(ct);
        var latest = activeRows
            .OrderByDescending(x => x.ActiveAtUtc)
            .FirstOrDefault();
        return latest?.PublicKeyHash;
    }
}
