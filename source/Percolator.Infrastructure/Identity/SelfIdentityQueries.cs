using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity;

public sealed class SelfIdentityQueries : ISelfIdentityQueries
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public SelfIdentityQueries(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<RatchetIdentityKey?> GetActiveIdentityFingerprintAsync(CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var bytes = await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.ActiveIdentityKeyFingerprint != null)
            .Select(x => x.ActiveIdentityKeyFingerprint)
            .FirstOrDefaultAsync(ct);

        return bytes is null ? null : RatchetIdentityKey.FromBytesOwned(bytes);
    }

    public async Task<ZkServerSecretParamsSeedBytes?> GetZkServerSecretParamsSeedAsync(CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var bytes = await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.ZkServerSecretParamsSeed != null)
            .Select(x => x.ZkServerSecretParamsSeed)
            .FirstOrDefaultAsync(ct);

        return bytes is null ? null : ZkServerSecretParamsSeedBytes.FromBytesOwned(bytes);
    }

    public async Task<(IdentityPublicKeyHash PublicKeyHash, PublicIdentityId PublicIdentityId)?> GetIdentityParticipantInfoAsync(SelfId selfIdentityId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var identity = await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.Id == selfIdentityId && x.ActiveIdentityKeyFingerprint != null)
            .Select(x => new { x.ActiveIdentityKeyFingerprint, PublicIdentityId = x.PublicIdentityId })
            .FirstOrDefaultAsync(ct);

        if (identity is null)
            return null;

        var pkh = IdentityPublicKeyHash.FromBytesOwned(identity.ActiveIdentityKeyFingerprint!);
        return (pkh, identity.PublicIdentityId);
    }
    public async Task<PublicIdentityId?> GetSelfIdentityPublicKeyAsync(SelfId selfIdentityId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var identity = await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.Id == selfIdentityId)
            .FirstOrDefaultAsync(ct);

        if (identity is null)
            return null;

        return identity.PublicIdentityId;
    }
}
