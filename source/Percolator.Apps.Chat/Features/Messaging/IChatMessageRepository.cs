using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public interface IChatMessageRepository
{
    Task AddAsync(ChatMessage record, CancellationToken ct = default);
    Task<ChatMessage?> GetByIdAsync(PayloadId id, CancellationToken ct = default);
    Task UpdateAsync(ChatMessage record, CancellationToken ct = default);
    Task<int> PurgeExpiredMessagesAsync(ulong currentTimestampUnixMs, CancellationToken ct = default);
}
