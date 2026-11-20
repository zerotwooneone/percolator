using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Domain.Common;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;

public sealed record ChunkVerifiedDomainEvent(
    TransferSessionId SessionId,
    ChunkIndex ChunkIndex,
    ChunkHash LeafHash,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
