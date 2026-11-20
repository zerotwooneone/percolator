using System.Collections.Concurrent;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class InMemoryChatMessageRepository : IChatMessageRepository
{
    private readonly ConcurrentDictionary<PayloadId, ChatMessage> _storage = new();

    public IReadOnlyCollection<ChatMessage> All => _storage.Values.ToList();

    public Task AddAsync(ChatMessage record, CancellationToken ct = default)
    {
        _storage[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<ChatMessage?> GetByIdAsync(PayloadId id, CancellationToken ct = default)
    {
        _storage.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<ChatMessage>> GetByChannelIdAsync(ChannelId channelId, CancellationToken ct = default)
    {
        var messages = _storage.Values
            .Where(m => m.ChannelId == channelId)
            .OrderBy(m => m.TimestampUnixMs)
            .ToList();
        return Task.FromResult<IReadOnlyList<ChatMessage>>(messages);
    }

    public Task UpdateAsync(ChatMessage record, CancellationToken ct = default)
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
