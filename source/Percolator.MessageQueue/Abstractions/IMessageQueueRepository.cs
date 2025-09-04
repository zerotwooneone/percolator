using Percolator.Identity;

namespace Percolator.MessageQueue.Abstractions;

public interface IMessageQueueRepository
{
    // Attempts to enqueue the message for the recipient. Returns (accepted, recipientCount, totalCount).
    // Implementations must enforce:
    // - Global limit: 10,000 total queued messages
    // - Per-recipient limit: 500
    // - Max blob size validation is performed in handler; repository may defensively re-check.
    Task<(bool Accepted, uint RecipientQueuedCount, uint TotalQueuedCount)> TryEnqueueAsync(
        PeerId recipientPeerId,
        byte[] messageBlob,
        CancellationToken cancellationToken);

    // Fetch up to maxCount oldest messages for the specified recipient and delete them atomically.
    // Returns the blobs in enqueue order (oldest first).
    Task<IReadOnlyList<byte[]>> FetchAndDeleteAsync(
        PeerId recipientPeerId,
        int maxCount,
        CancellationToken cancellationToken);
}
