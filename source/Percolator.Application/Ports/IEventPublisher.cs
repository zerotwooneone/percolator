namespace Percolator.Application.Ports;

public interface IEventPublisher
{
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IApplicationEvent;
}
