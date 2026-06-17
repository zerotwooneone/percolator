using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Cryptography;
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

        return bytes is null ? null : RatchetIdentityKey.FromBytes(bytes);
    }

    public async Task<byte[]?> GetZkServerSecretParamsSeedAsync(CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.ZkServerSecretParamsSeed != null)
            .Select(x => x.ZkServerSecretParamsSeed)
            .FirstOrDefaultAsync(ct);
    }
}
