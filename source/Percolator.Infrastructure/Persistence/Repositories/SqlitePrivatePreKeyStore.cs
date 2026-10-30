namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqlitePrivatePreKeyStore : IPrivatePreKeyStore
{
    private readonly PercolatorDbContext _dbContext;

    public SqlitePrivatePreKeyStore(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var ownerGuid = ownerId.Value;
        var entity = await _dbContext.VaultSignedPreKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value, cancellationToken);

        if (entity == null || entity.PrivateKeyCiphertext.Length == 0)
        {
            return null;
        }

        return EphemeralPrivateKey.FromSpan(entity.PrivateKeyCiphertext);
    }

    public async Task<EphemeralPrivateKey?> TryConsumeOneTimePreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        uint keyId,
        CancellationToken cancellationToken = default)
    {
        var ownerGuid = ownerId.Value;
        var entity = await _dbContext.VaultOneTimePreKeys
            .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value && k.KeyId == keyId, cancellationToken);

        if (entity == null || entity.IsConsumed || entity.PrivateKeyCiphertext.Length == 0)
        {
            return null;
        }

        var privateKey = EphemeralPrivateKey.FromSpan(entity.PrivateKeyCiphertext);

        // Enforce X3DH forward secrecy by zeroizing the stored ciphertext immediately upon consumption
        entity.IsConsumed = true;
        entity.ConsumedAtUtc = DateTimeOffset.UtcNow;
        entity.PrivateKeyCiphertext = Array.Empty<byte>();

        await _dbContext.SaveChangesAsync(cancellationToken);
        return privateKey;
    }

    public async Task StoreSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);

        var ownerGuid = ownerId.Value;
        var privBytes = privateKey.Span.ToArray();

        var existing = await _dbContext.VaultSignedPreKeys
            .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value, cancellationToken);

        if (existing == null)
        {
            var entity = new VaultSignedPreKeyEntity
            {
                OwnerIdentityId = ownerGuid,
                DeviceId = deviceId.Value,
                PrivateKeyCiphertext = privBytes,
                PublicKey = Array.Empty<byte>(),
                Signature = Array.Empty<byte>(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await _dbContext.VaultSignedPreKeys.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.PrivateKeyCiphertext = privBytes;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task StoreOneTimePreKeysPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        IEnumerable<(uint KeyId, EphemeralPrivateKey Key)> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var ownerGuid = ownerId.Value;
        var now = DateTimeOffset.UtcNow;

        foreach (var (keyId, privKey) in keys)
        {
            var privBytes = privKey.Span.ToArray();
            var existing = await _dbContext.VaultOneTimePreKeys
                .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value && k.KeyId == keyId, cancellationToken);

            if (existing == null)
            {
                var entity = new VaultOneTimePreKeyEntity
                {
                    OwnerIdentityId = ownerGuid,
                    DeviceId = deviceId.Value,
                    KeyId = keyId,
                    PrivateKeyCiphertext = privBytes,
                    PublicKey = Array.Empty<byte>(),
                    IsConsumed = false,
                    CreatedAtUtc = now
                };
                await _dbContext.VaultOneTimePreKeys.AddAsync(entity, cancellationToken);
            }
            else
            {
                existing.PrivateKeyCiphertext = privBytes;
                existing.IsConsumed = false;
                existing.ConsumedAtUtc = null;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
