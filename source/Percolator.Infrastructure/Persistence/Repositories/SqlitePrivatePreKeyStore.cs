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

/// <summary>Singleton over the pooled context factory — creates/disposes a context per call.</summary>
public sealed class SqlitePrivatePreKeyStore : IPrivatePreKeyStore
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public SqlitePrivatePreKeyStore(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    }

    public async Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        Domain.Identities.PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var ownerGuid = ownerId.Value;
        var entity = await db.VaultSignedPreKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value, cancellationToken);

        if (entity == null || entity.PrivateKey.Length == 0)
        {
            return null;
        }

        return EphemeralPrivateKey.FromSpan(entity.PrivateKey);
    }

    public async Task<EphemeralPrivateKey?> TryConsumeOneTimePreKeyPrivateAsync(
        Domain.Identities.PublicIdentityId ownerId,
        DeviceId deviceId,
        uint keyId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var ownerGuid = ownerId.Value;
        var entity = await db.VaultOneTimePreKeys
            .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value && k.KeyId == keyId, cancellationToken);

        if (entity == null || entity.IsConsumed || entity.PrivateKey.Length == 0)
        {
            return null;
        }

        var privateKey = EphemeralPrivateKey.FromSpan(entity.PrivateKey);

        // Enforce X3DH forward secrecy by zeroizing the stored key immediately upon consumption
        entity.IsConsumed = true;
        entity.ConsumedAtUtc = DateTimeOffset.UtcNow;
        entity.PrivateKey = Array.Empty<byte>();

        await db.SaveChangesAsync(cancellationToken);
        return privateKey;
    }

    public async Task<StoredSignedPreKey?> GetSignedPreKeyAsync(
        Domain.Identities.PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var ownerGuid = ownerId.Value;
        var entity = await db.VaultSignedPreKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value, cancellationToken);

        if (entity == null || entity.PrivateKey.Length == 0 || entity.PublicKey.Length == 0 || entity.Signature.Length == 0)
        {
            return null;
        }

        return new StoredSignedPreKey(
            EphemeralPrivateKey.FromSpan(entity.PrivateKey),
            DhPublicKey.FromSpan(entity.PublicKey),
            DeviceLinkProof.FromSpan(entity.Signature));
    }

    public async Task StoreSignedPreKeyAsync(
        Domain.Identities.PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        DhPublicKey publicKey,
        DeviceLinkProof signature,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(signature);

        var ownerGuid = ownerId.Value;
        var privBytes = privateKey.Span.ToArray();
        var pubBytes = publicKey.Span.ToArray();
        var sigBytes = signature.Span.ToArray();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.VaultSignedPreKeys
            .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value, cancellationToken);

        if (existing == null)
        {
            var entity = new VaultSignedPreKeyEntity
            {
                OwnerIdentityId = ownerGuid,
                DeviceId = deviceId.Value,
                PrivateKey = privBytes,
                PublicKey = pubBytes,
                Signature = sigBytes,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await db.VaultSignedPreKeys.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.PrivateKey = privBytes;
            existing.PublicKey = pubBytes;
            existing.Signature = sigBytes;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task StoreOneTimePreKeysAsync(
        Domain.Identities.PublicIdentityId ownerId,
        DeviceId deviceId,
        IEnumerable<OneTimePreKeyMaterial> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var ownerGuid = ownerId.Value;
        var now = DateTimeOffset.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var material in keys)
        {
            var privBytes = material.PrivateKey.Span.ToArray();
            var pubBytes = material.PublicKey.Span.ToArray();
            var existing = await db.VaultOneTimePreKeys
                .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value && k.KeyId == material.KeyId, cancellationToken);

            if (existing == null)
            {
                var entity = new VaultOneTimePreKeyEntity
                {
                    OwnerIdentityId = ownerGuid,
                    DeviceId = deviceId.Value,
                    KeyId = material.KeyId,
                    PrivateKey = privBytes,
                    PublicKey = pubBytes,
                    IsConsumed = false,
                    CreatedAtUtc = now
                };
                await db.VaultOneTimePreKeys.AddAsync(entity, cancellationToken);
            }
            else
            {
                existing.PrivateKey = privBytes;
                existing.PublicKey = pubBytes;
                existing.IsConsumed = false;
                existing.ConsumedAtUtc = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<uint> GetNextOneTimePreKeyIdAsync(
        Domain.Identities.PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var ownerGuid = ownerId.Value;
        // Include consumed rows so KeyIds are monotonically increasing and never reused.
        var maxId = await db.VaultOneTimePreKeys
            .Where(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value)
            .MaxAsync(k => (uint?)k.KeyId, cancellationToken);

        return (maxId ?? 0u) + 1u;
    }
}
