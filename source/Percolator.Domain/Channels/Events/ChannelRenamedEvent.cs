using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Channels.Events;

public sealed record ChannelRenamedEvent(
    ChannelId ChannelId,
    PublicIdentityId ActorId,
    string NewName,
    EpochNumber NewEpoch,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
