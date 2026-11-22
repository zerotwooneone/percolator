using Percolator.Domain.Identities.Events;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk.Events;
using Percolator.SourceGenerators;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Profiles.Events;

[MapFromDomainEvent(typeof(InboundContactRequestReceivedDomainEvent))]
public sealed partial record InboundContactRequestReceivedEvent : IApplicationEvent
{
    public Guid EventId { get; init; }
    public DateTimeOffset OccurredOnUtc { get; init; }
    public PublicIdentityId OwnerIdentityId { get; init; }
    public PublicIdentityId RemotePeerId { get; init; }
    public IdentityKey PrimaryPublicKey { get; init; } = null!;
}
