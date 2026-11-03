namespace Percolator.Application2.Ports;

public interface IEventSubscriber
{
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : IApplicationEvent;

    IDisposable RegisterFaultHandler(Action<EventDispatchError> handler);
}
