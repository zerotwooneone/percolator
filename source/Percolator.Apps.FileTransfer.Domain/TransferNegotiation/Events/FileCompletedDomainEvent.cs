using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Domain.Common;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;

public sealed record FileCompletedDomainEvent(
    TransferSessionId SessionId,
    FileIndex FileIndex,
    SanitizedRelativePath RelativePath,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
