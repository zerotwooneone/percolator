using Percolator.Domain.Common;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;

public sealed record TransferCancelledDomainEvent(
    TransferSessionId SessionId,
    TransferTerminationReason Reason,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
