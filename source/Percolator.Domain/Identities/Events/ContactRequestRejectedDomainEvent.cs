using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Identities.Events;

public sealed record ContactRequestRejectedDomainEvent(
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RemotePeerId,
    bool IsBlocked,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
