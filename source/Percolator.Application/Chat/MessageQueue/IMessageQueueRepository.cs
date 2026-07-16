using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.Application.Chat.MessageQueue;

/// <summary>
/// Mutation interface for message queue operations (write-only).
/// Separated from queries to support different access patterns.
/// </summary>
public interface IMessageQueueRepository
{
    // Attempts to enqueue the message for the recipient. Returns (accepted, recipientCount, totalCount).
    // Implementations must enforce:
    // - Global limit: 10,000 total queued messages
    // - Per-recipient limit: 500
    // - Max blob size validation is performed in handler; repository may defensively re-check.
    Task<(bool Accepted, uint RecipientQueuedCount, uint TotalQueuedCount)> TryEnqueueAsync(
        PublicIdentityId recipientPublicIdentityId,
        QueuedPayloadBytes messageBlob,
        CancellationToken cancellationToken);

    // Bulk enqueue the same message to multiple recipients. Returns (accepted, recipientCount, totalCount).
    // Implementations must enforce the same limits as TryEnqueueAsync.
    Task TryEnqueueBulkAsync(
        IReadOnlyList<PublicIdentityId> recipients,
        QueuedPayloadBytes messageBlob,
        CancellationToken cancellationToken);

    // Idempotent delete by AckId; returns true if a row was deleted, false if not found.
    Task<bool> DeleteByAckIdAsync(Guid ackId, CancellationToken cancellationToken);
}
