namespace Percolator.MessageQueue.Results;

public record FetchQueuedMessagesResult(IReadOnlyList<byte[]> Messages);
