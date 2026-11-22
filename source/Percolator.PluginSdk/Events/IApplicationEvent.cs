namespace Percolator.PluginSdk.Events;

public interface IApplicationEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredOnUtc { get; }
}
