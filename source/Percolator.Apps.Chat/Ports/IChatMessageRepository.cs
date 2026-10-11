using Percolator.Apps.Chat.Models;
using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.Apps.Chat.Ports;

public interface IChatMessageRepository
{
    Task AddAsync(ChatMessageRecord record, CancellationToken ct = default);
    Task<ChatMessageRecord?> GetByIdAsync(PayloadId id, CancellationToken ct = default);
    Task UpdateAsync(ChatMessageRecord record, CancellationToken ct = default);
    Task<int> PurgeExpiredMessagesAsync(ulong currentTimestampUnixMs, CancellationToken ct = default);
}
