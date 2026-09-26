using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Domain.Delivery.Events;

public sealed record EnvelopeBufferedEvent(
    Guid EnvelopeId,
    BlindedRoutingToken RecipientToken,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
