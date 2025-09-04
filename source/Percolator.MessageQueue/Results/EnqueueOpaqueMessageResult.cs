namespace Percolator.MessageQueue.Results;

public record EnqueueOpaqueMessageResult(
    bool Accepted,
    string? Error
);
