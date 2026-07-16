using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.Application.Chat.MessageQueue;

/// <summary>
/// Query interface for message queue operations (read-only).
/// Separated from mutations to support different access patterns.
/// </summary>
public interface IMessageQueueQueries
{
    /// <summary>
    /// Fetch up to maxCount oldest messages for the specified recipient WITHOUT deleting them.
    /// Returns (AckId, Blob) pairs in enqueue order (oldest first).
    /// </summary>
    Task<IReadOnlyList<(Guid AckId, QueuedPayloadBytes Blob)>> FetchAsync(
        PublicIdentityId recipientPublicIdentityId,
        int maxCount,
        CancellationToken cancellationToken);
}
