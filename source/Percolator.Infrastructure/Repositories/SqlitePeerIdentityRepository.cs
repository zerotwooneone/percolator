using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Repositories;

public sealed class SqlitePeerIdentityRepository : IPeerIdentityRepository
{
    private readonly PercolatorDbContext _db;
    public SqlitePeerIdentityRepository(PercolatorDbContext db) => _db = db;

    public async Task<PeerIdentity?> GetByIdAsync(PeerId id, CancellationToken ct = default)
    {
        var row = await _db.PeerIdentities.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PeerId == id.Value, ct);
        if (row == null) return null;

        var aggregate = new PeerIdentity(id);
        if (!string.IsNullOrWhiteSpace(row.Name))
            aggregate.SetDisplayName(row.Name);
        aggregate.SetVersionFromPersistence(row.Version);
        aggregate.SetLastKnownProfileRevision(row.LastKnownProfileRevision);

        var keys = await _db.PeerIdentityKeys_V2.AsNoTracking()
            .Where(k => k.PeerId == id.Value)
            .ToListAsync(ct);
        keys = keys.OrderBy(k => k.NotBeforeUtc).ToList();
        var now = DateTimeOffset.UtcNow;
        foreach (var k in keys)
        {
            aggregate.AddKey(k.PublicKeySpki, k.NotBeforeUtc, k.ExpiresAtUtc, now);
            // Note: Revocation not modeled in aggregate API yet beyond RevokedAt storage; skip for now
        }

        return aggregate;
    }

    public async Task<PeerIdentity?> GetByNameAsync(DisplayName name, CancellationToken ct = default)
    {
        var row = await _db.PeerIdentities.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == name.Value, ct);
        if (row == null) return null;
        return await GetByIdAsync(new PeerId(row.PeerId), ct);
    }

    public async Task<PeerIdentity?> FindByPublicKeyHashAsync(byte[] fingerprint, CancellationToken ct = default)
    {
        var row = await _db.PeerIdentityKeys_V2.AsNoTracking()
            .Where(k => k.Fingerprint != null && k.Fingerprint.SequenceEqual(fingerprint))
            .Select(k => k.PeerId)
            .FirstOrDefaultAsync(ct);
        if (row == Guid.Empty) return null;
        return await GetByIdAsync(new PeerId(row), ct);
    }

    public async Task SaveAsync(PeerIdentity peer, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await _db.PeerIdentities
            .FirstOrDefaultAsync(p => p.PeerId == peer.Id.Value, ct);

        if (existing == null)
        {
            var insert = new PeerIdentityDbo
            {
                PeerId = peer.Id.Value,
                Name = peer.DisplayName?.Value ?? string.Empty,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            await _db.PeerIdentities.AddAsync(insert, ct);

            // Clear and insert keys
            foreach (var k in peer.Keys)
            {
                await _db.PeerIdentityKeys_V2.AddAsync(new PeerIdentityKeyDbo_V2
                {
                    PeerId = peer.Id.Value,
                    PublicKeySpki = k.Spki,
                    Fingerprint = k.Fingerprint,
                    NotBeforeUtc = k.NotBefore,
                    ExpiresAtUtc = k.ExpiresAt,
                    RevokedAtUtc = null
                }, ct);
            }

            await _db.SaveChangesAsync(ct);
            peer.SetVersionFromPersistence(1);
            return;
        }

        // Update path with optimistic concurrency
        if (peer.Version != existing.Version)
            throw new InvalidOperationException("Concurrency conflict saving PeerIdentity.");

        existing.Name = peer.DisplayName?.Value ?? string.Empty;
        existing.Version = existing.Version + 1;
        existing.UpdatedAtUtc = now;
        existing.LastKnownProfileRevision = peer.LastKnownProfileRevision;

        // Replace key set to reflect aggregate state
        var oldKeys = _db.PeerIdentityKeys_V2.Where(k => k.PeerId == peer.Id.Value);
        _db.PeerIdentityKeys_V2.RemoveRange(oldKeys);
        foreach (var k in peer.Keys)
        {
            await _db.PeerIdentityKeys_V2.AddAsync(new PeerIdentityKeyDbo_V2
            {
                PeerId = peer.Id.Value,
                PublicKeySpki = k.Spki,
                Fingerprint = k.Fingerprint,
                NotBeforeUtc = k.NotBefore,
                ExpiresAtUtc = k.ExpiresAt,
                RevokedAtUtc = null
            }, ct);
        }

        await _db.SaveChangesAsync(ct);
        peer.SetVersionFromPersistence(existing.Version);
    }
}
