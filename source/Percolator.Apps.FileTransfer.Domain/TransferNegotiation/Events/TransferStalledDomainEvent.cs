using Percolator.Domain.Common;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;

public sealed record TransferStalledDomainEvent(
    TransferSessionId SessionId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
