using Percolator.Apps.Chat.Models.ReadModels;
using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.Apps.Chat.Ports;

public interface IChatMessageQueryService
{
    Task<IReadOnlyList<ChatMessageReadModel>> GetMessagesAsync(
        ChannelId channelId,
        ulong? beforeTimestampMs,
        int limit,
        ulong currentTimestampMs,
        CancellationToken ct = default);
}
