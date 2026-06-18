using Percolator.Application.Chat.MessageQueue.Results;

namespace Percolator.Application.Chat.MessageQueue;

public interface IMessageQueueService
{
    Task<EnqueueOpaqueMessageResult> EnqueueOpaqueAsync(
        byte[] recipientPublicKeyHash,
        byte[] messageBlob,
        CancellationToken cancellationToken = default);
}
