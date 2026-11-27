using System.Collections.Concurrent;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Ingress;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

internal sealed class InMemoryUnknownGroupCache : IUnknownGroupMessageCacheRepository
{
    private readonly ConcurrentDictionary<PayloadId, UnknownGroupMessageRecord> _records = new();

    public Task AddAsync(UnknownGroupMessageRecord record, CancellationToken ct = default)
    {
        _records[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UnknownGroupMessageRecord>> GetByChannelIdAsync(ChannelId channelId, CancellationToken ct = default)
    {
        var list = _records.Values.Where(r => r.ChannelId == channelId).ToList();
        return Task.FromResult<IReadOnlyList<UnknownGroupMessageRecord>>(list);
    }

    public Task RemoveAsync(PayloadId id, CancellationToken ct = default)
    {
        _records.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task<int> PurgeExpiredAsync(ulong currentTimestampUnixMs, CancellationToken ct = default)
    {
        int count = 0;
        foreach (var kvp in _records)
        {
            if (kvp.Value.ExpiresAtUnixMs <= currentTimestampUnixMs && _records.TryRemove(kvp.Key, out _))
            {
                count++;
            }
        }
        return Task.FromResult(count);
    }
}
