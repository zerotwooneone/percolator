using System.Collections.Concurrent;
using Percolator.Apps.Chat.Models;
using Percolator.Apps.Chat.Ports;
using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class InMemoryChatMessageRepository : IChatMessageRepository
{
    private readonly ConcurrentDictionary<PayloadId, ChatMessageRecord> _storage = new();

    public IReadOnlyCollection<ChatMessageRecord> All => _storage.Values.ToList();

    public Task AddAsync(ChatMessageRecord record, CancellationToken ct = default)
    {
        _storage[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<ChatMessageRecord?> GetByIdAsync(PayloadId id, CancellationToken ct = default)
    {
        _storage.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    public Task UpdateAsync(ChatMessageRecord record, CancellationToken ct = default)
    {
        _storage[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<int> PurgeExpiredMessagesAsync(ulong currentTimestampUnixMs, CancellationToken ct = default)
    {
        int purgedCount = 0;
        foreach (var kvp in _storage)
        {
            if (kvp.Value.IsExpired(currentTimestampUnixMs))
            {
                if (_storage.TryRemove(kvp.Key, out _))
                {
                    purgedCount++;
                }
            }
        }

        return Task.FromResult(purgedCount);
    }
}
