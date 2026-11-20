using Percolator.Domain.Common;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;

public sealed record SwarmMemberJoinedDomainEvent(
    TransferSessionId SessionId,
    PublicIdentityId PeerId,
    Bitfield InitialBitfield,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
