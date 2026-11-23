namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteRelayPreKeyRepository : IRelayPreKeyRepository
{
    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SqliteRelayPreKeyRepository(IDbContextFactory<PercolatorDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    private SemaphoreSlim GetLock(byte[] routingBytes, uint deviceId)
    {
        string key = $"{Convert.ToHexString(routingBytes)}:{deviceId}";
        return _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    private async Task<TResult> WithContextAsync<TResult>(Func<PercolatorDbContext, Task<TResult>> action)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await action(context).ConfigureAwait(false);
    }

    public Task<RelayHostedPreKeyBundle?> GetAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var routingBytes = routingToken.ToByteArray();

        return WithContextAsync(async dbContext =>
        {
            var entity = await dbContext.RelayPreKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.RoutingToken == routingBytes && k.DeviceId == deviceId.Value, cancellationToken);

            if (entity == null) return null;

            var opkEntities = await dbContext.RelayOneTimePreKeys
                .AsNoTracking()
                .Where(k => k.OwnerRoutingToken == routingBytes && k.DeviceId == deviceId.Value)
                .OrderBy(k => k.PreKeyId)
                .ToListAsync(cancellationToken);

            var opks = opkEntities.Select(o => (o.PreKeyId, DhPublicKey.FromSpan(o.PublicKey))).ToList();
            return MapEntityToDomain(entity, opks);
        });
    }

    public Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForMailboxAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default)
    {
        var routingBytes = routingToken.ToByteArray();

        return WithContextAsync<IReadOnlyList<RelayHostedPreKeyBundle>>(async dbContext =>
        {
            var entities = await dbContext.RelayPreKeys
                .AsNoTracking()
                .Where(k => k.RoutingToken == routingBytes)
                .ToListAsync(cancellationToken);

            var allOpks = await dbContext.RelayOneTimePreKeys
                .AsNoTracking()
                .Where(k => k.OwnerRoutingToken == routingBytes)
                .OrderBy(k => k.PreKeyId)
                .ToListAsync(cancellationToken);

            var opksByDevice = allOpks.GroupBy(o => o.DeviceId).ToDictionary(g => g.Key, g => g.ToList());

            var result = new List<RelayHostedPreKeyBundle>(entities.Count);
            foreach (var entity in entities)
            {
                var opkList = opksByDevice.TryGetValue(entity.DeviceId, out var list)
                    ? list.Select(o => (o.PreKeyId, DhPublicKey.FromSpan(o.PublicKey))).ToList()
                    : new List<(uint, DhPublicKey)>();
                result.Add(MapEntityToDomain(entity, opkList));
            }

            return result;
        });
    }

    public Task<bool> HasActivePreKeyAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default)
    {
        var routingBytes = routingToken.ToByteArray();
        return WithContextAsync(async dbContext =>
        {
            return await dbContext.RelayPreKeys
                .AsNoTracking()
                .AnyAsync(k => k.RoutingToken == routingBytes, cancellationToken);
        });
    }

    public async Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var routingBytes = bundle.RoutingToken.ToByteArray();
        var idKeyBytes = bundle.IdentityKey.Span.ToArray();
        var signedKeyBytes = bundle.SignedPreKey.Span.ToArray();
        var sigBytes = bundle.SignedPreKeySignature.Span.ToArray();
        var now = DateTimeOffset.UtcNow;
        var sem = GetLock(routingBytes, bundle.DeviceId.Value);

        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var existing = await dbContext.RelayPreKeys
                .FirstOrDefaultAsync(k => k.RoutingToken == routingBytes && k.DeviceId == bundle.DeviceId.Value, cancellationToken);

            if (existing == null)
            {
                var entity = new RelayPreKeyEntity
                {
                    RoutingToken = routingBytes,
                    DeviceId = bundle.DeviceId.Value,
                    IdentityKey = idKeyBytes,
                    SignedPreKey = signedKeyBytes,
                    SignedPreKeySignature = sigBytes,
                    SignedPreKeyId = bundle.SignedPreKeyId.Value,
                    UpdatedAtUtc = now
                };
                await dbContext.RelayPreKeys.AddAsync(entity, cancellationToken);
            }
            else
            {
                existing.IdentityKey = idKeyBytes;
                existing.SignedPreKey = signedKeyBytes;
                existing.SignedPreKeySignature = sigBytes;
                existing.SignedPreKeyId = bundle.SignedPreKeyId.Value;
                existing.UpdatedAtUtc = now;
            }

            var existingOpks = await dbContext.RelayOneTimePreKeys
                .Where(k => k.OwnerRoutingToken == routingBytes && k.DeviceId == bundle.DeviceId.Value)
                .ToListAsync(cancellationToken);

            var currentKeyIds = bundle.OneTimePreKeys.Select(k => k.KeyId).ToHashSet();
            var toRemove = existingOpks.Where(k => !currentKeyIds.Contains(k.PreKeyId)).ToList();
            if (toRemove.Count > 0)
            {
                dbContext.RelayOneTimePreKeys.RemoveRange(toRemove);
            }

            var existingKeyIds = existingOpks.Select(k => k.PreKeyId).ToHashSet();
            var newOpks = bundle.OneTimePreKeys
                .Where(k => !existingKeyIds.Contains(k.KeyId))
                .Select(k => new RelayOneTimePreKeyEntity
                {
                    OwnerRoutingToken = routingBytes,
                    DeviceId = bundle.DeviceId.Value,
                    PreKeyId = k.KeyId,
                    PublicKey = k.Key.Span.ToArray()
                });

            await dbContext.RelayOneTimePreKeys.AddRangeAsync(newOpks, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task DeleteAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var routingBytes = routingToken.ToByteArray();
        var sem = GetLock(routingBytes, deviceId.Value);

        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await dbContext.RelayPreKeys
                .FirstOrDefaultAsync(k => k.RoutingToken == routingBytes && k.DeviceId == deviceId.Value, cancellationToken);

            if (existing != null)
            {
                dbContext.RelayPreKeys.Remove(existing);
            }

            var opks = await dbContext.RelayOneTimePreKeys
                .Where(k => k.OwnerRoutingToken == routingBytes && k.DeviceId == deviceId.Value)
                .ToListAsync(cancellationToken);

            if (opks.Count > 0)
            {
                dbContext.RelayOneTimePreKeys.RemoveRange(opks);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<PreKeyBundle?> ConsumePreKeyBundleAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var routingBytes = routingToken.ToByteArray();
        var sem = GetLock(routingBytes, deviceId.Value);

        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var dbContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var bundleHeader = await dbContext.RelayPreKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.RoutingToken == routingBytes && k.DeviceId == deviceId.Value, cancellationToken);

            if (bundleHeader == null)
            {
                return null;
            }

            var opk = await dbContext.RelayOneTimePreKeys
                .Where(k => k.OwnerRoutingToken == routingBytes && k.DeviceId == deviceId.Value)
                .OrderBy(k => k.PreKeyId)
                .FirstOrDefaultAsync(cancellationToken);

            if (opk != null)
            {
                dbContext.RelayOneTimePreKeys.Remove(opk);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var idKey = IdentityKey.FromSpan(bundleHeader.IdentityKey);
            var signedPreKey = DhPublicKey.FromSpan(bundleHeader.SignedPreKey);
            var signedPreKeySig = DeviceLinkProof.FromSpan(bundleHeader.SignedPreKeySignature);
            var signedPreKeyId = new SignedPreKeyId(bundleHeader.SignedPreKeyId == 0 ? 1u : bundleHeader.SignedPreKeyId);
            var otpkKey = opk != null ? DhPublicKey.FromSpan(opk.PublicKey) : null;
            uint otpkId = opk?.PreKeyId ?? 0;

            return new PreKeyBundle(deviceId, idKey, signedPreKey, signedPreKeySig, signedPreKeyId, otpkKey, otpkId);
        }
        finally
        {
            sem.Release();
        }
    }

    private static RelayHostedPreKeyBundle MapEntityToDomain(RelayPreKeyEntity entity, IEnumerable<(uint KeyId, DhPublicKey Key)> opks)
    {
        var routingToken = BlindedRoutingToken.FromBytes(entity.RoutingToken);
        var deviceId = new DeviceId(entity.DeviceId);
        var idKey = IdentityKey.FromSpan(entity.IdentityKey);
        var signedKey = DhPublicKey.FromSpan(entity.SignedPreKey);
        var sig = DeviceLinkProof.FromSpan(entity.SignedPreKeySignature);

        return RelayHostedPreKeyBundle.Rehydrate(
            routingToken,
            deviceId,
            idKey,
            signedKey,
            sig,
            new SignedPreKeyId(entity.SignedPreKeyId == 0 ? 1u : entity.SignedPreKeyId),
            opks);
    }
}
