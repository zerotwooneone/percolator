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
        SignedPreKeyId? signedPreKeyId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var ownerGuid = ownerId.Value;
        var entity = signedPreKeyId.HasValue
            ? await db.VaultSignedPreKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value && k.KeyId == signedPreKeyId.Value.Value,
                    cancellationToken)
            : await db.VaultSignedPreKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value && k.IsCurrent,
                    cancellationToken);

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
            .FirstOrDefaultAsync(
                k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value && k.IsCurrent,
                cancellationToken);

        if (entity == null || entity.PrivateKey.Length == 0 || entity.PublicKey.Length == 0 || entity.Signature.Length == 0)
        {
            return null;
        }

        return new StoredSignedPreKey(
            new SignedPreKeyId(entity.KeyId),
            EphemeralPrivateKey.FromSpan(entity.PrivateKey),
            DhPublicKey.FromSpan(entity.PublicKey),
            DeviceLinkProof.FromSpan(entity.Signature),
            entity.CreatedAtUtc);
    }

    public async Task<SignedPreKeyId> StoreSignedPreKeyAsync(
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

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Newest-wins: the stored key gets the next monotonically increasing id and
        // becomes current; every prior generation is demoted but retained until the
        // retention prune so in-flight handshakes keep decrypting.
        var nextKeyId = (await db.VaultSignedPreKeys
                .Where(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value)
                .MaxAsync(k => (uint?)k.KeyId, cancellationToken) ?? 0u) + 1u;

        await db.VaultSignedPreKeys
            .Where(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value && k.IsCurrent)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.IsCurrent, false), cancellationToken);

        var entity = new VaultSignedPreKeyEntity
        {
            OwnerIdentityId = ownerGuid,
            DeviceId = deviceId.Value,
            KeyId = nextKeyId,
            IsCurrent = true,
            PrivateKey = privateKey.Span.ToArray(),
            PublicKey = publicKey.Span.ToArray(),
            Signature = signature.Span.ToArray(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await db.VaultSignedPreKeys.AddAsync(entity, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return new SignedPreKeyId(nextKeyId);
    }

    public async Task<int> PruneSignedPreKeysAsync(
        Domain.Identities.PublicIdentityId ownerId,
        DeviceId deviceId,
        DateTimeOffset olderThanUtc,
        int minRetainedCount = 3,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var ownerGuid = ownerId.Value;

        var generations = await db.VaultSignedPreKeys
            .Where(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value)
            .OrderByDescending(k => k.KeyId)
            .ToListAsync(cancellationToken);

        // Retain at least the newest minRetainedCount generations and always the
        // current one; everything older than the retention window is pruned.
        var candidates = generations
            .Where(k => !k.IsCurrent && k.CreatedAtUtc < olderThanUtc)
            .ToList();
        var retained = generations.Count - candidates.Count;
        var toDelete = candidates
            .OrderBy(k => k.KeyId) // oldest first — the newest candidates stay retained
            .Take(Math.Max(0, candidates.Count - Math.Max(0, minRetainedCount - retained)))
            .ToList();

        if (toDelete.Count > 0)
        {
            var deleteKeyIds = toDelete.Select(k => k.KeyId).ToList();
            // Zeroize via UPDATE before the DELETE — EF issues DELETE-only on
            // Remove, so clearing the tracked entities would never reach the
            // file and freed cells would keep private-key bytes.
            await db.VaultSignedPreKeys
                .Where(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value && deleteKeyIds.Contains(k.KeyId))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.PrivateKey, new byte[0]), cancellationToken);
            db.VaultSignedPreKeys.RemoveRange(toDelete);
            await db.SaveChangesAsync(cancellationToken);
        }

        return toDelete.Count;
    }

    public async Task<int> CountAvailableOneTimePreKeysAsync(
        Domain.Identities.PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var ownerGuid = ownerId.Value;
        return await db.VaultOneTimePreKeys
            .Where(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value && !k.IsConsumed)
            .CountAsync(cancellationToken);
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
