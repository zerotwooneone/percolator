using Percolator.Domain.Identities.Events;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk.Events;
using Percolator.SourceGenerators;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Profiles.Events;

[MapFromDomainEvent(typeof(ContactRequestRejectedDomainEvent))]
public sealed partial record ContactRequestRejectedEvent : IApplicationEvent
{
    public Guid EventId { get; init; }
    public DateTimeOffset OccurredOnUtc { get; init; }
    public PublicIdentityId OwnerIdentityId { get; init; }
    public PublicIdentityId RemotePeerId { get; init; }
    public bool IsBlocked { get; init; }
}
