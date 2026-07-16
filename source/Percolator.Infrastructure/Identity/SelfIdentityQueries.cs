using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;
using DeviceId = Percolator.Identity.DeviceId;

namespace Percolator.Infrastructure.Identity;

public sealed class SelfIdentityQueries : ISelfIdentityQueries
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public SelfIdentityQueries(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<ZkServerSecretParamsSeedBytes?> GetZkServerSecretParamsSeedAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var bytes = await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.ZkServerSecretParamsSeed != null)
            .Select(x => x.ZkServerSecretParamsSeed)
            .FirstOrDefaultAsync(ct);

        return bytes is null ? null : ZkServerSecretParamsSeedBytes.FromBytesOwned(bytes);
    }

    public async Task<(IdentityPublicKeyHash PublicKeyHash, PublicIdentityId PublicIdentityId)?> GetIdentityParticipantInfoAsync(SelfId selfIdentityId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var identity = await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.Id == selfIdentityId.Value && x.ActiveIdentityKeyFingerprint != null)
            .Select(x => new { x.ActiveIdentityKeyFingerprint, PublicIdentityId = x.PublicIdentityId })
            .FirstOrDefaultAsync(ct);

        if (identity is null)
            return null;

        var pkh = IdentityPublicKeyHash.FromBytesOwned(identity.ActiveIdentityKeyFingerprint!);
        return (pkh, new PublicIdentityId(identity.PublicIdentityId));
    }
    public async Task<PublicIdentityId?> GetSelfIdentityPublicKeyAsync(SelfId selfIdentityId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var identity = await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.Id == selfIdentityId.Value)
            .FirstOrDefaultAsync(ct);

        if (identity is null)
            return null;

        return new PublicIdentityId(identity.PublicIdentityId);
    }

    public async Task<(PublicIdentityId PublicIdentityId, DeviceId DeviceId)?> GetSelfIdentityCryptoInfoAsync(SelfId selfIdentityId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var identity = await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.Id == selfIdentityId.Value)
            .Select(x => new { x.PublicIdentityId, x.DeviceId })
            .FirstOrDefaultAsync(ct);

        if (identity is null)
            return null;

        return (new PublicIdentityId(identity.PublicIdentityId), new DeviceId(identity.DeviceId));
    }
    
    public async Task<(PublicIdentityId PublicIdentityId, DeviceId DeviceId, RatchetIdentityKey? Fingerprint)?> GetSelfIdentityCertInfoAsync(SelfId selfIdentityId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var identity = await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.Id == selfIdentityId.Value)
            .Select(x => new { x.PublicIdentityId, x.DeviceId, x.ActiveIdentityKeyFingerprint })
            .FirstOrDefaultAsync(ct);

        if (identity is null)
            return null;

        return (
            new PublicIdentityId(identity.PublicIdentityId), 
            new DeviceId(identity.DeviceId), 
            identity.ActiveIdentityKeyFingerprint is null ? null : RatchetIdentityKey.FromBytesOwned(identity.ActiveIdentityKeyFingerprint));
    }

    public async Task<SelfId?> GetSelfIdByPublicIdentityIdAsync(PublicIdentityId publicIdentityId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var identity = await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.PublicIdentityId == publicIdentityId.Value)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(ct);

        if (identity == 0)
            return null;

        return new SelfId(identity);
    }
}
