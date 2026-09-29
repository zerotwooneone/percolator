using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Channels.Events;

public sealed record ChannelMemberRoleChangedEvent(
    ChannelId ChannelId,
    PublicIdentityId TargetMemberId,
    ChannelRole NewRole,
    EpochNumber NewEpoch,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
