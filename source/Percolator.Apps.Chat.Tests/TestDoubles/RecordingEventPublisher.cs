using Percolator.PluginSdk.Events;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

internal sealed class RecordingEventPublisher : IEventPublisher
{
    public List<IApplicationEvent> PublishedEvents { get; } = [];

    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IApplicationEvent
    {
        PublishedEvents.Add(@event);
        return ValueTask.CompletedTask;
    }
}
