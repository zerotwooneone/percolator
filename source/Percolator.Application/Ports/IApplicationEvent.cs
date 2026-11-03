namespace Percolator.Application2.Ports;

public interface IApplicationEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredOnUtc { get; }
}
