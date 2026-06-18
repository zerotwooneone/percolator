namespace Percolator.Application.Chat.MessageQueue.Results;

public record EnqueueOpaqueMessageResult(
    bool Accepted,
    string? Error
);
