namespace Percolator.Domain.Identities.Events;

using System;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;

public sealed record ContactOutboundAcceptedDomainEvent(
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RemotePeerId,
    PeerTrustLevel TrustLevel,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
