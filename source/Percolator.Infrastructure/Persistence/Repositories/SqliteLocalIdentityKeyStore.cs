using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

/// <summary>Singleton over the pooled context factory — creates/disposes a context per call.</summary>
public sealed class SqliteLocalIdentityKeyStore : ILocalIdentityKeyStore
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public SqliteLocalIdentityKeyStore(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    }

    public async Task<EphemeralPrivateKey?> GetIdentityPrivateKeyAsync(Domain.Identities.PublicIdentityId identityId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var idGuid = identityId.Value;
        var entity = await db.VaultIdentityKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == idGuid, ct);

        if (entity == null || entity.PrivateKey.Length == 0)
        {
            return null;
        }

        return EphemeralPrivateKey.FromSpan(entity.PrivateKey);
    }

    public async Task<IdentityKey?> GetIdentityPublicKeyAsync(Domain.Identities.PublicIdentityId identityId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var idGuid = identityId.Value;
        var entity = await db.VaultIdentityKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == idGuid, ct);

        if (entity == null || entity.PublicKey.Length != 32)
        {
            return null;
        }

        return IdentityKey.FromSpan(entity.PublicKey);
    }

    public async Task<ProfileKey?> GetProfileKeyAsync(Domain.Identities.PublicIdentityId identityId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var idGuid = identityId.Value;
        var entity = await db.VaultIdentityKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == idGuid, ct);

        if (entity == null || entity.ProfileKey.Length != 32)
        {
            return null;
        }

        return ProfileKey.FromSpan(entity.ProfileKey);
    }

    public async Task SaveIdentityKeysAsync(
        Domain.Identities.PublicIdentityId identityId,
        EphemeralPrivateKey privateKey,
        IdentityKey publicKey,
        ProfileKey profileKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(profileKey);

        var idGuid = identityId.Value;
        var privBytes = privateKey.Span.ToArray();
        var pubBytes = publicKey.Span.ToArray();
        var profileBytes = profileKey.Span.ToArray();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.VaultIdentityKeys
            .FirstOrDefaultAsync(k => k.Id == idGuid, ct);

        if (existing == null)
        {
            var entity = new VaultIdentityKeyEntity
            {
                Id = idGuid,
                PrivateKey = privBytes,
                PublicKey = pubBytes,
                ProfileKey = profileBytes,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await db.VaultIdentityKeys.AddAsync(entity, ct);
        }
        else
        {
            existing.PrivateKey = privBytes;
            existing.PublicKey = pubBytes;
            existing.ProfileKey = profileBytes;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<ProfileKey> RotateProfileKeyAsync(Domain.Identities.PublicIdentityId identityId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var idGuid = identityId.Value;
        var existing = await db.VaultIdentityKeys
            .FirstOrDefaultAsync(k => k.Id == idGuid, ct)
            ?? throw new InvalidOperationException($"No identity keys stored for '{identityId.Value}'.");

        var rotated = ProfileKey.Generate();
        existing.ProfileKey = rotated.Span.ToArray();
        await db.SaveChangesAsync(ct);
        return rotated;
    }

    /// <summary>Permanently removes the stored identity keypair (persona shred path).</summary>
    public async Task DeleteIdentityKeysAsync(Domain.Identities.PublicIdentityId identityId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var idGuid = identityId.Value;
        await db.VaultIdentityKeys
            .Where(k => k.Id == idGuid)
            .ExecuteDeleteAsync(ct);
    }
}
