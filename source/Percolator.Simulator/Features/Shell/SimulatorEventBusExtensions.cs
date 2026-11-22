namespace Percolator.Simulator.Features.Shell;

using System;
using System.Threading.Tasks;
using Percolator.PluginSdk.Events;
using R3;

public static class SimulatorEventBusExtensions
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
}
