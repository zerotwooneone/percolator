namespace Percolator.Domain.Channels.Events;

using System;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

public sealed record DirectChannelActivatedDomainEvent(
    ChannelId ChannelId,
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RemotePeerId,
    RelayEndpoint RelayEndpoint,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
