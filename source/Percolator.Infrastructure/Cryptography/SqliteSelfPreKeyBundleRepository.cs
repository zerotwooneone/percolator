using System.Linq;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.KeyExchange;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Cryptography;

internal sealed class SqliteSelfPreKeyBundleRepository : ISelfPreKeyBundleRepository
{
    private readonly PercolatorDbContext _db;
    private static readonly byte[] Entropy = new byte[] { 0x42, 0x77, 0xA3, 0x19, 0x5F, 0xC0, 0xD4, 0xEE };

    public SqliteSelfPreKeyBundleRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task SaveSignedPreKeyAsync(int selfIdentityId, Guid signedPreKeyId, byte[] signedPreKeyPrivate, byte[] signedPreKeyPublicSpki, byte[] preKeySignature, DateTimeOffset expires, CancellationToken ct = default)
    {
        var encPriv = Protect(signedPreKeyPrivate);
        var existing = await _db.SelfPreKeySigned.AsNoTracking().FirstOrDefaultAsync(x => x.SelfIdentityId == selfIdentityId && x.SignedPreKeyId == signedPreKeyId, ct);
        if (existing is null)
        {
            _db.SelfPreKeySigned.Add(new SelfPreKeySignedDbo
            {
                SelfIdentityId = selfIdentityId,
                SignedPreKeyId = signedPreKeyId,
                SignedPreKeyPrivate = encPriv,
                SignedPreKeyPublicSpki = signedPreKeyPublicSpki,
                PreKeySignature = preKeySignature,
                ExpiresUtc = expires
            });
        }
        else
        {
            var tracked = await _db.SelfPreKeySigned.FirstAsync(x => x.Id == existing.Id, ct);
            tracked.SignedPreKeyPrivate = encPriv;
            tracked.SignedPreKeyPublicSpki = signedPreKeyPublicSpki;
            tracked.PreKeySignature = preKeySignature;
            tracked.ExpiresUtc = expires;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveOneTimePreKeysAsync(int selfIdentityId, IEnumerable<(Guid otkId, byte[] otkPrivate, byte[] otkPublicSpki)> oneTimePreKeys, CancellationToken ct = default)
    {
        foreach (var (otkId, priv, spki) in oneTimePreKeys)
        {
            var exists = await _db.SelfOneTimePreKeys.AsNoTracking().AnyAsync(x => x.SelfIdentityId == selfIdentityId && x.OneTimePreKeyId == otkId, ct);
            if (!exists)
            {
                _db.SelfOneTimePreKeys.Add(new SelfOneTimePreKeyDbo
                {
                    SelfIdentityId = selfIdentityId,
                    OneTimePreKeyId = otkId,
                    OneTimePreKeyPrivate = Protect(priv),
                    OneTimePreKeyPublicSpki = spki
                });
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(byte[] spkPrivate, byte[] spkPublicSpki, byte[] preKeySignature, DateTimeOffset expires)?> TryGetSignedPreKeyAsync(int selfIdentityId, Guid signedPreKeyId, CancellationToken ct = default)
    {
        var rec = await _db.SelfPreKeySigned.AsNoTracking().FirstOrDefaultAsync(x => x.SelfIdentityId == selfIdentityId && x.SignedPreKeyId == signedPreKeyId, ct);
        if (rec is null) return null;
        return (Unprotect(rec.SignedPreKeyPrivate), rec.SignedPreKeyPublicSpki, rec.PreKeySignature, rec.ExpiresUtc);
    }

    public async Task<byte[]?> TryPopOneTimePreKeyPrivateAsync(int selfIdentityId, Guid oneTimePreKeyId, CancellationToken ct = default)
    {
        // transactional select+delete to ensure single-use
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var rec = await _db.SelfOneTimePreKeys.FirstOrDefaultAsync(x => x.SelfIdentityId == selfIdentityId && x.OneTimePreKeyId == oneTimePreKeyId, ct);
            if (rec is null)
            {
                await tx.RollbackAsync(ct);
                return null;
            }
            var priv = Unprotect(rec.OneTimePreKeyPrivate);
            _db.SelfOneTimePreKeys.Remove(rec);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return priv;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<(Guid otkId, byte[] otkPublicSpki)?> TryReserveOneTimePreKeyAsync(
        int selfIdentityId,
        Guid requestCorrelationId,
        DateTimeOffset reservedUntilUtc,
        CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Correlation id must be unique while reserved (duplicates rejected)
            var existingReservation = await _db.SelfOneTimePreKeys.AsNoTracking()
                .AnyAsync(x => x.SelfIdentityId == selfIdentityId && x.ReservedForRequestCorrelationId == requestCorrelationId, ct);
            if (existingReservation)
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            // Find any unreserved OTK
            var rec = await _db.SelfOneTimePreKeys
                .FirstOrDefaultAsync(x => x.SelfIdentityId == selfIdentityId && x.ReservedForRequestCorrelationId == null, ct);
            if (rec is null)
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            rec.ReservedForRequestCorrelationId = requestCorrelationId;
            rec.ReservedUntilUtc = reservedUntilUtc;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (rec.OneTimePreKeyId, rec.OneTimePreKeyPublicSpki);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<byte[]?> TryConsumeReservedOneTimePreKeyPrivateAsync(
        int selfIdentityId,
        Guid requestCorrelationId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var rec = await _db.SelfOneTimePreKeys
                .FirstOrDefaultAsync(x => x.SelfIdentityId == selfIdentityId && x.ReservedForRequestCorrelationId == requestCorrelationId, ct);
            if (rec is null)
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            if (rec.ReservedUntilUtc is not null && rec.ReservedUntilUtc.Value <= nowUtc)
            {
                // Expired reservation: delete and do not return secrets.
                _db.SelfOneTimePreKeys.Remove(rec);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return null;
            }

            var priv = Unprotect(rec.OneTimePreKeyPrivate);
            _db.SelfOneTimePreKeys.Remove(rec);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return priv;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<int> PurgeExpiredReservedOneTimePreKeysAsync(
        int selfIdentityId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        // NOTE: EFCore+Sqlite can fail to translate DateTimeOffset comparisons depending on provider/version.
        // Load reserved candidates for this identity, then perform the time comparison in-memory.
        var reserved = await _db.SelfOneTimePreKeys
            .Where(x => x.SelfIdentityId == selfIdentityId
                        && x.ReservedForRequestCorrelationId != null
                        && x.ReservedUntilUtc != null)
            .ToListAsync(ct);

        var expired = reserved
            .Where(x => x.ReservedUntilUtc!.Value <= nowUtc)
            .ToList();

        if (expired.Count == 0)
        {
            return 0;
        }

        _db.SelfOneTimePreKeys.RemoveRange(expired);
        await _db.SaveChangesAsync(ct);
        return expired.Count;
    }

    public async Task<bool> TryBurnReservedOneTimePreKeyAsync(
        int selfIdentityId,
        Guid requestCorrelationId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var rec = await _db.SelfOneTimePreKeys
                .FirstOrDefaultAsync(x => x.SelfIdentityId == selfIdentityId && x.ReservedForRequestCorrelationId == requestCorrelationId, ct);
            if (rec is null)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            // Burn/delete without returning private key material.
            _db.SelfOneTimePreKeys.Remove(rec);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    private static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
