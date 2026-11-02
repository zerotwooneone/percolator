namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.Chat.Ingress;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqlUnknownGroupMessageCacheRepository : IUnknownGroupMessageCacheRepository
{
    public const int MaxCacheCapacity = 500;
    private readonly PercolatorDbContext _dbContext;

    public SqlUnknownGroupMessageCacheRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(UnknownGroupMessageRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Enforce bounded capacity limit (max 500 cached envelopes)
        int currentCount = await _dbContext.ChatUnknownGroupMessages.CountAsync(ct);
        if (currentCount >= MaxCacheCapacity)
        {
            int toEvictCount = currentCount - MaxCacheCapacity + 1;
            var oldestToEvict = await _dbContext.ChatUnknownGroupMessages
                .OrderBy(m => (long)m.ReceivedAtUnixMs)
                .Take(toEvictCount)
                .ToListAsync(ct);

            if (oldestToEvict.Count > 0)
            {
                _dbContext.ChatUnknownGroupMessages.RemoveRange(oldestToEvict);
            }
        }

        var entity = new ChatUnknownGroupMessageEntity
        {
            Id = record.Id.Value,
            ChannelId = record.ChannelId.Value,
            AuthorId = record.AuthorId.ToByteArray(),
            EncryptedPayload = record.EncryptedPayload.ToArray(),
            ReceivedAtUnixMs = record.ReceivedAtUnixMs,
            ExpiresAtUnixMs = record.ExpiresAtUnixMs
        };

        _dbContext.ChatUnknownGroupMessages.Add(entity);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UnknownGroupMessageRecord>> GetByChannelIdAsync(ChannelId channelId, CancellationToken ct = default)
    {
        var channelGuid = channelId.Value;
        var entities = await _dbContext.ChatUnknownGroupMessages
            .AsNoTracking()
            .Where(m => m.ChannelId == channelGuid)
            .OrderBy(m => (long)m.ReceivedAtUnixMs)
            .ToListAsync(ct);

        return entities.Select(ToDomainModel).ToList();
    }

    public async Task RemoveAsync(PayloadId id, CancellationToken ct = default)
    {
        var guid = id.Value;
        var entity = await _dbContext.ChatUnknownGroupMessages
            .FirstOrDefaultAsync(m => m.Id == guid, ct);

        if (entity != null)
        {
            _dbContext.ChatUnknownGroupMessages.Remove(entity);
            await _dbContext.SaveChangesAsync(ct);
        }
    }

    public async Task<int> PurgeExpiredAsync(ulong currentTimestampUnixMs, CancellationToken ct = default)
    {
        var expired = await _dbContext.ChatUnknownGroupMessages
            .Where(m => (long)m.ExpiresAtUnixMs <= (long)currentTimestampUnixMs)
            .ToListAsync(ct);

        if (expired.Count == 0)
        {
            return 0;
        }

        _dbContext.ChatUnknownGroupMessages.RemoveRange(expired);
        await _dbContext.SaveChangesAsync(ct);
        return expired.Count;
    }

    private static UnknownGroupMessageRecord ToDomainModel(ChatUnknownGroupMessageEntity entity)
    {
        return new UnknownGroupMessageRecord(
            PayloadId.FromGuid(entity.Id),
            ChannelId.FromGuid(entity.ChannelId),
            PublicIdentityId.FromBytes(entity.AuthorId),
            entity.EncryptedPayload,
            entity.ReceivedAtUnixMs,
            entity.ExpiresAtUnixMs);
    }
}
