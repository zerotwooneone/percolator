using System.Threading;
using System.Threading.Tasks;
using Percolator.MessageQueue.Results;

namespace Percolator.MessageQueue.Abstractions;

public interface IMessageQueueService
{
    Task<EnqueueOpaqueMessageResult> EnqueueOpaqueAsync(
        byte[] recipientPublicKeyHash,
        byte[] messageBlob,
        CancellationToken cancellationToken = default);
}
