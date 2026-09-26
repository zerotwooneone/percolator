using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Identities.Events;

public sealed record IdentityCreatedEvent(
    PublicIdentityId IdentityId,
    IdentityRole Role,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
