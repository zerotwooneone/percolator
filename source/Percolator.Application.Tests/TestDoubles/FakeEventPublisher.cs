using Percolator.Application.Ports;

namespace Percolator.Application.Tests.TestDoubles;

public sealed class FakeEventPublisher : IEventPublisher
{
    private readonly List<IApplicationEvent> _publishedEvents = new();
    public IReadOnlyList<IApplicationEvent> PublishedEvents => _publishedEvents;

    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IApplicationEvent
    {
        _publishedEvents.Add(@event);
        return ValueTask.CompletedTask;
    }
}
