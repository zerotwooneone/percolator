using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Domain.Common;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;

public sealed record TransferOfferedDomainEvent(
    TransferSessionId SessionId,
    PublicIdentityId InitiatorId,
    TransferBundleSummary Summary,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
