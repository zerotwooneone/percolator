using Percolator.Domain.Channels.Events;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk.Events;
using Percolator.SourceGenerators;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Channels.Events;

[MapFromDomainEvent(typeof(DirectChannelCreatedDomainEvent))]
public sealed partial record DirectChannelCreatedEvent : IApplicationEvent
{
    public Guid EventId { get; init; }
    public DateTimeOffset OccurredOnUtc { get; init; }
    public ChannelId ChannelId { get; init; }
    public PublicIdentityId OwnerIdentityId { get; init; }
    public PublicIdentityId RemotePeerId { get; init; }
    public DirectChannelStatus Status { get; init; }
    public RelayEndpoint? RelayEndpoint { get; init; }
}
