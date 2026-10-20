using Percolator.Domain.Common;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;

public sealed record TransferAcceptedLocallyDomainEvent(
    TransferSessionId SessionId,
    SelectedFileIndices SelectedFiles,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
