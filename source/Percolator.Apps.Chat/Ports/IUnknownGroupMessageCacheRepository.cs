using Percolator.Apps.Chat.Models;
using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.Apps.Chat.Ports;

public interface IUnknownGroupMessageCacheRepository
{
    Task AddAsync(UnknownGroupMessageRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<UnknownGroupMessageRecord>> GetByChannelIdAsync(ChannelId channelId, CancellationToken ct = default);
    Task RemoveAsync(PayloadId id, CancellationToken ct = default);
    Task<int> PurgeExpiredAsync(ulong currentTimestampUnixMs, CancellationToken ct = default);
}
