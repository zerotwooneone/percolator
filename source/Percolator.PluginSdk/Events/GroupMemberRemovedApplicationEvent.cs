using Percolator.Domain.Channels.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.PluginSdk.Events;

/// <summary>
/// Application-layer event published when a member is removed from a Group V2 channel and the
/// channel epoch advances (E -&gt; E+1). Vertical apps (such as File Transfer) subscribe to revoke
/// evicted peers' pinned transport credentials and tear down active peer connections immediately.
/// </summary>
public sealed record GroupMemberRemovedApplicationEvent(
    ChannelId ChannelId,
    PublicIdentityId MemberId,
    EpochNumber NewEpoch,
    DateTimeOffset OccurredOnUtc) : IApplicationEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
