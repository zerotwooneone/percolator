namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteRelayPreKeyRepository : IRelayPreKeyRepository
{
    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;

    public SqliteRelayPreKeyRepository(IDbContextFactory<PercolatorDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    private async Task<TResult> WithContextAsync<TResult>(Func<PercolatorDbContext, Task<TResult>> action)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await action(context).ConfigureAwait(false);
    }

    public Task<RelayHostedPreKeyBundle?> GetAsync(PublicIdentityId ownerId, DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var ownerGuid = ownerId.Value;

        return WithContextAsync(async dbContext =>
        {
            var entity = await dbContext.RelayPreKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value, cancellationToken);

            return entity != null ? MapEntityToDomain(entity) : null;
        });
    }

    public Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForIdentityAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
    {
        var ownerGuid = ownerId.Value;

        return WithContextAsync<IReadOnlyList<RelayHostedPreKeyBundle>>(async dbContext =>
        {
            var entities = await dbContext.RelayPreKeys
                .AsNoTracking()
                .Where(k => k.OwnerIdentityId == ownerGuid)
                .ToListAsync(cancellationToken);

            return entities.Select(MapEntityToDomain).ToList();
        });
    }

    public Task<bool> HasActivePreKeyAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
    {
        var ownerGuid = ownerId.Value;
        return WithContextAsync(async dbContext =>
        {
            return await dbContext.RelayPreKeys
                .AsNoTracking()
                .AnyAsync(k => k.OwnerIdentityId == ownerGuid, cancellationToken);
        });
    }

    public Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var ownerGuid = bundle.OwnerId.Value;
        var idKeyBytes = bundle.IdentityKey.Span.ToArray();
        var signedKeyBytes = bundle.SignedPreKey.Span.ToArray();
        var sigBytes = bundle.SignedPreKeySignature.Span.ToArray();
        var opkJson = SerializeOpks(bundle.OneTimePreKeys);
        var now = DateTimeOffset.UtcNow;

        return WithContextAsync(async dbContext =>
        {
            var existing = await dbContext.RelayPreKeys
                .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == bundle.DeviceId.Value, cancellationToken);

            if (existing == null)
            {
                var entity = new RelayPreKeyEntity
                {
                    OwnerIdentityId = ownerGuid,
                    DeviceId = bundle.DeviceId.Value,
                    IdentityKey = idKeyBytes,
                    SignedPreKey = signedKeyBytes,
                    SignedPreKeySignature = sigBytes,
                    OneTimePreKeysJson = opkJson,
                    UpdatedAtUtc = now
                };
                await dbContext.RelayPreKeys.AddAsync(entity, cancellationToken);
            }
            else
            {
                existing.IdentityKey = idKeyBytes;
                existing.SignedPreKey = signedKeyBytes;
                existing.SignedPreKeySignature = sigBytes;
                existing.OneTimePreKeysJson = opkJson;
                existing.UpdatedAtUtc = now;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        });
    }

    public Task DeleteAsync(PublicIdentityId ownerId, DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var ownerGuid = ownerId.Value;
        return WithContextAsync(async dbContext =>
        {
            var existing = await dbContext.RelayPreKeys
                .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value, cancellationToken);

            if (existing != null)
            {
                dbContext.RelayPreKeys.Remove(existing);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return true;
        });
    }

    private static RelayHostedPreKeyBundle MapEntityToDomain(RelayPreKeyEntity entity)
    {
        var ownerId = new PublicIdentityId(entity.OwnerIdentityId);
        var deviceId = new DeviceId(entity.DeviceId);
        var idKey = IdentityKey.FromSpan(entity.IdentityKey);
        var signedKey = DhPublicKey.FromSpan(entity.SignedPreKey);
        var sig = DeviceLinkProof.FromSpan(entity.SignedPreKeySignature);
        var opks = DeserializeOpks(entity.OneTimePreKeysJson);

        return RelayHostedPreKeyBundle.Rehydrate(
            ownerId,
            deviceId,
            idKey,
            signedKey,
            sig,
            opks);
    }

    private static string SerializeOpks(IEnumerable<(uint KeyId, DhPublicKey Key)> opks)
    {
        var dtos = opks.Select(o => new OpkDto
        {
            KeyId = o.KeyId,
            KeyHex = Convert.ToHexString(o.Key.Span)
        }).ToList();
        return JsonSerializer.Serialize(dtos);
    }

    private static List<(uint KeyId, DhPublicKey Key)> DeserializeOpks(string json)
    {
        if (string.IsNullOrEmpty(json)) return new List<(uint, DhPublicKey)>();

        try
        {
            var dtos = JsonSerializer.Deserialize<List<OpkDto>>(json);
            if (dtos == null) return new List<(uint, DhPublicKey)>();

            return dtos.Select(d => (d.KeyId, DhPublicKey.FromSpan(Convert.FromHexString(d.KeyHex)))).ToList();
        }
        catch
        {
            return new List<(uint, DhPublicKey)>();
        }
    }

    private sealed class OpkDto
    {
        public uint KeyId { get; set; }
        public string KeyHex { get; set; } = string.Empty;
    }
}
