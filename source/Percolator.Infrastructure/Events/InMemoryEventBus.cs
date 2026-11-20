using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Events;

public sealed partial class InMemoryEventBus : IEventBus, IAsyncDisposable, IDisposable
{
    private readonly ILogger<InMemoryEventBus> _logger;
    private readonly ConcurrentDictionary<Type, ImmutableList<SubscriptionEntry>> _subscriptions = new();
    private readonly ConcurrentDictionary<Type, IReadOnlyList<Type>> _hierarchyCache = new();
    private readonly ConcurrentDictionary<Guid, Action<EventDispatchError>> _faultHandlers = new();

    private readonly Channel<EventDispatchError> _faultChannel = Channel.CreateBounded<EventDispatchError>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _faultProcessingTask;
    private int _disposed;

    public InMemoryEventBus(ILogger<InMemoryEventBus> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _faultProcessingTask = ProcessFaultsAsync(_cts.Token);
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : IApplicationEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var entry = new SubscriptionEntry(
            handler,
            (evt, ct) => handler((TEvent)evt, ct));

        var eventType = typeof(TEvent);

        _subscriptions.AddOrUpdate(
            eventType,
            _ => ImmutableList.Create(entry),
            (_, currentList) => currentList.Add(entry));

        return new SubscriptionToken(this, eventType, entry);
    }

    public IDisposable RegisterFaultHandler(Action<EventDispatchError> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var id = Guid.NewGuid();
        _faultHandlers[id] = handler;
        return new FaultSubscriptionToken(this, id);
    }

    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IApplicationEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        cancellationToken.ThrowIfCancellationRequested();

        if (_subscriptions.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        var actualType = @event.GetType();
        var hierarchy = _hierarchyCache.GetOrAdd(actualType, static type => GetEventHierarchy(type));

        var hasSubscribers = false;
        for (var i = 0; i < hierarchy.Count; i++)
        {
            if (_subscriptions.TryGetValue(hierarchy[i], out var list) && !list.IsEmpty)
            {
                hasSubscribers = true;
                break;
            }
        }

        if (!hasSubscribers)
        {
            return ValueTask.CompletedTask;
        }

        return PublishCoreAsync(@event, actualType, hierarchy, cancellationToken);
    }

    private async ValueTask PublishCoreAsync<TEvent>(
        TEvent @event,
        Type actualType,
        IReadOnlyList<Type> hierarchy,
        CancellationToken cancellationToken) where TEvent : IApplicationEvent
    {
        var totalDispatched = 0;

        for (var i = 0; i < hierarchy.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_subscriptions.TryGetValue(hierarchy[i], out var subscribers) || subscribers.IsEmpty)
            {
                continue;
            }

            for (var j = 0; j < subscribers.Count; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sub = subscribers[j];
                try
                {
                    await sub.Invoker(@event, cancellationToken).ConfigureAwait(false);
                    totalDispatched++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Subscriber faulted handling event {EventType}", actualType.Name);
                    _faultChannel.Writer.TryWrite(new EventDispatchError(@event, sub.OriginalHandler, ex));
                }
            }
        }

        LogEventDispatched(actualType.Name, totalDispatched);
    }

    private async Task ProcessFaultsAsync(CancellationToken ct)
    {
        try
        {
            while (await _faultChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_faultChannel.Reader.TryRead(out var error))
                {
                    DispatchFault(error);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in fault processing loop");
        }
    }

    private void DispatchFault(EventDispatchError error)
    {
        foreach (var handler in _faultHandlers.Values)
        {
            try
            {
                handler(error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fault handler threw an unhandled exception");
            }
        }
    }

    private void Unsubscribe(Type eventType, SubscriptionEntry entry)
    {
        while (_subscriptions.TryGetValue(eventType, out var currentList))
        {
            var updatedList = currentList.Remove(entry);
            if (_subscriptions.TryUpdate(eventType, updatedList, currentList))
            {
                break;
            }
        }
    }

    private void UnregisterFaultHandler(Guid id)
    {
        _faultHandlers.TryRemove(id, out _);
    }

    internal int GetSubscriberCount<TEvent>() where TEvent : IApplicationEvent
    {
        return _subscriptions.TryGetValue(typeof(TEvent), out var list) ? list.Count : 0;
    }

    internal int GetFaultHandlerCount() => _faultHandlers.Count;

    [LoggerMessage(LogLevel.Trace, "Dispatched event {EventType} to {SubscriberCount} subscriber(s).")]
    private partial void LogEventDispatched(string eventType, int subscriberCount);

    private static IReadOnlyList<Type> GetEventHierarchy(Type type)
    {
        var result = new List<Type>();
        var appEventType = typeof(IApplicationEvent);

        var current = type;
        while (current != null && current != typeof(object))
        {
            if (appEventType.IsAssignableFrom(current))
            {
                result.Add(current);
            }
            current = current.BaseType;
        }

        foreach (var iface in type.GetInterfaces())
        {
            if (appEventType.IsAssignableFrom(iface) && !result.Contains(iface))
            {
                result.Add(iface);
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _faultChannel.Writer.TryComplete();
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _faultChannel.Writer.TryComplete();

        try
        {
            await _faultProcessingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private sealed class SubscriptionEntry
    {
        public Delegate OriginalHandler { get; }
        public Func<IApplicationEvent, CancellationToken, ValueTask> Invoker { get; }

        public SubscriptionEntry(Delegate originalHandler, Func<IApplicationEvent, CancellationToken, ValueTask> invoker)
        {
            OriginalHandler = originalHandler;
            Invoker = invoker;
        }
    }

    private sealed class SubscriptionToken : IDisposable
    {
        private InMemoryEventBus? _bus;
        private readonly Type _eventType;
        private readonly SubscriptionEntry _entry;

        public SubscriptionToken(InMemoryEventBus bus, Type eventType, SubscriptionEntry entry)
        {
            _bus = bus;
            _eventType = eventType;
            _entry = entry;
        }

        public void Dispose()
        {
            var bus = Interlocked.Exchange(ref _bus, null);
            if (bus is null) return;

            bus.Unsubscribe(_eventType, _entry);
        }
    }

    private sealed class FaultSubscriptionToken : IDisposable
    {
        private InMemoryEventBus? _bus;
        private readonly Guid _id;

        public FaultSubscriptionToken(InMemoryEventBus bus, Guid id)
        {
            _bus = bus;
            _id = id;
        }

        public void Dispose()
        {
            var bus = Interlocked.Exchange(ref _bus, null);
            if (bus is null) return;

            bus.UnregisterFaultHandler(_id);
        }
    }
}
