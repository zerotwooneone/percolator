using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Tests.Maintenance;

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
