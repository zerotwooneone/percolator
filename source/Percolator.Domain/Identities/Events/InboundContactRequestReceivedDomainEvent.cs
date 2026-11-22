using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Identities.Events;

public sealed record InboundContactRequestReceivedDomainEvent(
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RemotePeerId,
    IdentityKey PrimaryPublicKey,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
