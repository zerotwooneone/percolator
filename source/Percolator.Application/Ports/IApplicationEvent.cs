namespace Percolator.Application.Ports;

public interface IApplicationEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredOnUtc { get; }
}
