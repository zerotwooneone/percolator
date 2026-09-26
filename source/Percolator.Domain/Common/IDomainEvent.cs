namespace Percolator.Domain.Common;

public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredOnUtc { get; }
}
