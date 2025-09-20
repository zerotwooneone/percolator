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
                active.ExpiredAtUtc = nowUtc;
                _db.PeerPublicSigningKeys.Update(active);
                await _db.SaveChangesAsync(ct);
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
}
