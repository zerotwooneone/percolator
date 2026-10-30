namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteLocalIdentityKeyStore : ILocalIdentityKeyStore
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteLocalIdentityKeyStore(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<byte[]?> GetIdentityPrivateKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        var idGuid = identityId.Value;
        var entity = await _dbContext.VaultIdentityKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == idGuid, ct);

        return entity?.PrivateKeyCiphertext;
    }

    public async Task<IdentityKey?> GetIdentityPublicKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
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

    public async Task SaveKeyAsync(PublicIdentityId identityId, byte[] privateKey, IdentityKey publicKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentNullException.ThrowIfNull(publicKey);

        var idGuid = identityId.Value;
        var pubBytes = publicKey.Span.ToArray();

        var existing = await _dbContext.VaultIdentityKeys
            .FirstOrDefaultAsync(k => k.Id == idGuid, ct);

        if (existing == null)
        {
            var entity = new VaultIdentityKeyEntity
            {
                Id = idGuid,
                PrivateKeyCiphertext = (byte[])privateKey.Clone(),
                PublicKey = pubBytes,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await _dbContext.VaultIdentityKeys.AddAsync(entity, ct);
        }
        else
        {
            existing.PrivateKeyCiphertext = (byte[])privateKey.Clone();
            existing.PublicKey = pubBytes;
        }

        await _dbContext.SaveChangesAsync(ct);
    }
}
