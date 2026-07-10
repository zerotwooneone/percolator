using Percolator.Application.Chat.MessageQueue.Results;
using Percolator.Identity;

namespace Percolator.Application.Chat.MessageQueue;

public interface IMessageQueueService
{
    Task<EnqueueOpaqueMessageResult> EnqueueOpaqueAsync(
        PublicIdentityId recipientPublicIdentityId,
        byte[] messageBlob,
        CancellationToken cancellationToken = default);
}
