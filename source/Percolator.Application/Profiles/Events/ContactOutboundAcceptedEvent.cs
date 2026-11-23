namespace Percolator.Application.Profiles.Events;

using System;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Events;
using Percolator.Domain.Identities.Model;
using Percolator.PluginSdk.Events;
using Percolator.SourceGenerators;

[MapFromDomainEvent(typeof(ContactOutboundAcceptedDomainEvent))]
public sealed partial record ContactOutboundAcceptedEvent : IApplicationEvent
{
    public Guid EventId { get; init; }
    public DateTimeOffset OccurredOnUtc { get; init; }
    public PublicIdentityId OwnerIdentityId { get; init; }
    public PublicIdentityId RemotePeerId { get; init; }
    public PeerTrustLevel TrustLevel { get; init; }
}
