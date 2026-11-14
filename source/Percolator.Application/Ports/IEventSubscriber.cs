namespace Percolator.Application.Ports;

public interface IEventSubscriber
{
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : IApplicationEvent;

    IDisposable RegisterFaultHandler(Action<EventDispatchError> handler);
}
