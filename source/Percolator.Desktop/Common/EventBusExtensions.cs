using System;
using System.Threading.Tasks;
using Percolator.Application2.Ports;
using R3;

namespace Percolator.Desktop.Common;

public static class EventBusExtensions
{
    public static Observable<TEvent> ObserveEvent<TEvent>(this IEventSubscriber subscriber) where TEvent : IApplicationEvent
    {
        ArgumentNullException.ThrowIfNull(subscriber);

        return Observable.Create<TEvent>(observer =>
        {
            var subscription = subscriber.Subscribe<TEvent>((evt, ct) =>
            {
                if (ct.IsCancellationRequested)
                {
                    return ValueTask.CompletedTask;
                }

                observer.OnNext(evt);
                return ValueTask.CompletedTask;
            });

            return subscription;
        });
    }

    public static Observable<TEvent> ObserveEventOnUIThread<TEvent>(this IEventSubscriber subscriber) where TEvent : IApplicationEvent
    {
        return subscriber.ObserveEvent<TEvent>()
            .ObserveOnCurrentSynchronizationContext();
    }
}
