namespace Percolator.Application.Chat.MessageQueue.Results;

public record FetchQueuedMessagesResult(IReadOnlyList<byte[]> Messages);
