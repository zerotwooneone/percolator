using Percolator.Domain.Channels.Events;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.PluginSdk.Events;
using Percolator.SourceGenerators;

namespace Percolator.Application.Channels.Events;

[MapFromDomainEvent(typeof(DirectChannelActivatedDomainEvent))]
public sealed partial record DirectChannelActivatedEvent : IApplicationEvent
{
    public Guid EventId { get; init; }
    public DateTimeOffset OccurredOnUtc { get; init; }
    public ChannelId ChannelId { get; init; }
    public PublicIdentityId OwnerIdentityId { get; init; }
    public PublicIdentityId RemotePeerId { get; init; }
    public RelayEndpoint? RelayEndpoint { get; init; }
}
