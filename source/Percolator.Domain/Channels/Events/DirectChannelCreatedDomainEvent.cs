using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Channels.Events;

public sealed record DirectChannelCreatedDomainEvent(
    ChannelId ChannelId,
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RemotePeerId,
    DirectChannelStatus Status,
    RelayEndpoint? RelayEndpoint,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
