using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteLocalIdentityKeyStore : ILocalIdentityKeyStore
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteLocalIdentityKeyStore(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<EphemeralPrivateKey?> GetIdentityPrivateKeyAsync(Domain.Identities.PublicIdentityId identityId, CancellationToken ct = default)
    {
        var idGuid = identityId.Value;
        var entity = await _dbContext.VaultIdentityKeys
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
        var idGuid = identityId.Value;
        var entity = await _dbContext.VaultIdentityKeys
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
        var idGuid = identityId.Value;
        var entity = await _dbContext.VaultIdentityKeys
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

        var existing = await _dbContext.VaultIdentityKeys
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
            await _dbContext.VaultIdentityKeys.AddAsync(entity, ct);
        }
        else
        {
            existing.PrivateKey = privBytes;
            existing.PublicKey = pubBytes;
            existing.ProfileKey = profileBytes;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<ProfileKey> RotateProfileKeyAsync(Domain.Identities.PublicIdentityId identityId, CancellationToken ct = default)
    {
        var idGuid = identityId.Value;
        var existing = await _dbContext.VaultIdentityKeys
            .FirstOrDefaultAsync(k => k.Id == idGuid, ct)
            ?? throw new InvalidOperationException($"No identity keys stored for '{identityId.Value}'.");

        var rotated = ProfileKey.Generate();
        existing.ProfileKey = rotated.Span.ToArray();
        await _dbContext.SaveChangesAsync(ct);
        return rotated;
    }

    /// <summary>Permanently removes the stored identity keypair (persona shred path).</summary>
    public async Task DeleteIdentityKeysAsync(Domain.Identities.PublicIdentityId identityId, CancellationToken ct = default)
    {
        var idGuid = identityId.Value;
        await _dbContext.VaultIdentityKeys
            .Where(k => k.Id == idGuid)
            .ExecuteDeleteAsync(ct);
    }
}
