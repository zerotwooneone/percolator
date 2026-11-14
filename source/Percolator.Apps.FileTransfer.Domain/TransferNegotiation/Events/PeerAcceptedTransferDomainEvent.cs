using Percolator.Domain.Common;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;

public sealed record PeerAcceptedTransferDomainEvent(
    TransferSessionId SessionId,
    PublicIdentityId RecipientId,
    Bitfield ResumptionBitfield,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
