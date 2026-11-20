using Percolator.Apps.Chat.Ingress;
using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.Application2IntegrationTests.Fakes;

/// <summary>
/// Minimal in-memory unknown-group message cache for integration nodes.
/// </summary>
public sealed class InMemoryUnknownGroupMessageCacheRepository : IUnknownGroupMessageCacheRepository
{
    private readonly List<UnknownGroupMessageRecord> _records = new();
    private readonly object _lock = new();

    public Task AddAsync(UnknownGroupMessageRecord record, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _records.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UnknownGroupMessageRecord>> GetByChannelIdAsync(ChannelId channelId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<UnknownGroupMessageRecord>>(
                _records.Where(r => r.ChannelId == channelId).ToList());
        }
    }

    public Task RemoveAsync(PayloadId id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _records.RemoveAll(r => r.Id == id);
        }

        return Task.CompletedTask;
    }

    public Task<int> PurgeExpiredAsync(ulong currentTimestampUnixMs, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_records.RemoveAll(r => r.ExpiresAtUnixMs <= currentTimestampUnixMs));
        }
    }
}
